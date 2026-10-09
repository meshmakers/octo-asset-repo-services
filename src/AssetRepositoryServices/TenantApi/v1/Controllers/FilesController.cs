using Asp.Versioning;
using Duende.IdentityModel;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Files;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Services.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.TenantApi.v1.Controllers;

/// <summary>
///     Byte streaming of the platform file system (AB#6171, decision Q1): upload and download of file
///     contents by path (<c>{root}/{folders…}/{name}</c>, root = well-known name of the folder root) or by
///     rtId, folder statistics and the capabilities of the cluster. Metadata — roots, folder listings,
///     create folder, rename, move, delete, links — stays on the generic GraphQL API (System.Files).
/// </summary>
/// <remarks>
///     Access like any entity: the scope policies of the tenant API plus data permissions. Every read runs
///     in the caller's session, so entries hidden by data permissions answer 404 like missing ones; writes
///     go through the engine's data-permission write guard (403 FORBIDDEN). Errors are problem details with
///     a stable <c>code</c> extension (ROOT_NOT_FOUND, PATH_NOT_FOUND, AMBIGUOUS_PATH, NAME_CONFLICT,
///     NOT_A_FOLDER, NOT_A_FILE, LIMIT_EXCEEDED, FORBIDDEN, INVALID_NAME, INVALID_REQUEST).
/// </remarks>
[Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
[Route("{tenantId:tenantId}/v{version:apiVersion}/files")]
[ApiController]
[ApiVersion("1.0")]
// ReSharper disable once ClassNeverInstantiated.Global
public class FilesController(
    IOctoService octoService,
    FileSystemService fileSystem,
    FileZipService zipService,
    IOptions<FilesOptions> filesOptions,
    ConstructionKit.Contracts.Services.ICkCacheService ckCacheService,
    ILogger<FilesController> logger) : ControllerBase
{
    private static readonly FileExtensionContentTypeProvider ContentTypeProvider = new();

    private FilesOptions Limits => filesOptions.Value;

    /// <summary>
    ///     Limits and features of the file API on this cluster.
    /// </summary>
    [HttpGet("capabilities")]
    [Authorize(AuthenticationSchemes = InfrastructureCommon.OidcAuthenticationScheme,
        Policy = AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(FilesCapabilitiesDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCapabilities()
    {
        // Tenant-aware: the file system exists only when System.Files is in the tenant's CK model.
        var tenantId = HttpContext.GetTenantId();
        var available = false;
        if (!string.IsNullOrEmpty(tenantId))
        {
            await octoService.SystemContext.FindTenantRepositoryAsync(tenantId);
            available = ckCacheService.TryGetRtCkType(tenantId, FileSystemService.FileType, out _);
        }

        return Ok(new FilesCapabilitiesDto
        {
            Available = available,
            Reason = available ? null : "SYSTEM_FILES_MISSING",
            MaxDeleteEntries = Limits.MaxDeleteEntries,
            MaxUploadBytes = Limits.MaxUploadBytes,
            ZipDownload = true,
            ZipMaxFiles = Limits.ZipMaxFiles,
            ZipMaxBytes = Limits.ZipMaxBytes,
            PreviewMaxBytes = Limits.PreviewMaxBytes,
            ReservedRootNames = FileSystemConstants.ReservedRootWellKnownNames.Order().ToList()
        });
    }

    /// <summary>
    ///     Number of links of each entry to other entities (any association except the folder structure, any
    ///     role, any direction) — the "linked" marker per row. Entries the caller cannot see are left out.
    /// </summary>
    [HttpPost("linked-counts")]
    [Authorize(AuthenticationSchemes = InfrastructureCommon.OidcAuthenticationScheme,
        Policy = AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(LinkedCountsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public Task<IActionResult> GetLinkedCounts([FromBody] LinkedCountsRequestDto request)
    {
        return ExecuteAsync(async (repository, session, _) =>
        {
            if (request.RtIds.Count > 500)
            {
                throw FileSystemException.InvalidRequest("At most 500 rtIds per request.");
            }

            var ids = new List<Meshmakers.Octo.ConstructionKit.Contracts.OctoObjectId>();
            foreach (var rtId in request.RtIds.Distinct())
            {
                if (!Meshmakers.Octo.ConstructionKit.Contracts.OctoObjectId.TryParse(rtId, out var id))
                {
                    throw FileSystemException.InvalidRequest($"'{rtId}' is not a valid rtId.");
                }

                ids.Add(id);
            }

            return Ok(new LinkedCountsDto
            {
                Counts = await fileSystem.GetLinkedCountsAsync(repository, session, ids, HttpContext.RequestAborted)
            });
        });
    }

    /// <summary>
    ///     Deep statistics of a root or folder addressed by path (counts, bytes, linked files, entries hidden
    ///     by data permissions).
    /// </summary>
    /// <param name="root">Well-known name of the folder root</param>
    /// <param name="path">Folder path below the root; empty for the root itself</param>
    [HttpGet("stats")]
    [Authorize(AuthenticationSchemes = InfrastructureCommon.OidcAuthenticationScheme,
        Policy = AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(FolderStatsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public Task<IActionResult> GetStatsByPath([FromQuery] string root, [FromQuery] string? path)
    {
        return ExecuteAsync(async (repository, session, unfiltered) =>
        {
            var folder = await fileSystem.ResolveAsync(repository, session, root, path);
            return Ok(await fileSystem.GetStatsAsync(repository, session, unfiltered, folder,
                HttpContext.RequestAborted));
        });
    }

    /// <summary>
    ///     Deep statistics of a root or folder addressed by rtId.
    /// </summary>
    [HttpGet("items/{rtId}/stats")]
    [Authorize(AuthenticationSchemes = InfrastructureCommon.OidcAuthenticationScheme,
        Policy = AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(FolderStatsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public Task<IActionResult> GetStatsById(string rtId)
    {
        return ExecuteAsync(async (repository, session, unfiltered) =>
        {
            var folder = await FindByIdAsync(repository, session, rtId);
            return Ok(await fileSystem.GetStatsAsync(repository, session, unfiltered, folder,
                HttpContext.RequestAborted));
        });
    }

    /// <summary>
    ///     Downloads a file addressed by rtId.
    /// </summary>
    /// <param name="rtId">Runtime id of the file</param>
    /// <param name="inline">Ask for inline delivery (preview); active content (SVG, HTML, …) stays an attachment</param>
    [HttpGet("items/{rtId}/content")]
    [Authorize(AuthenticationSchemes = InfrastructureCommon.OidcAuthenticationScheme,
        Policy = AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public Task<IActionResult> DownloadById(string rtId, [FromQuery] bool inline = false)
    {
        return ExecuteAsync(async (repository, session, _) =>
        {
            var file = await FindByIdAsync(repository, session, rtId);
            return await DownloadAsync(repository, session, file, inline);
        });
    }

    /// <summary>
    ///     Uploads a file into the folder or root addressed by rtId. The request body is the raw content,
    ///     <c>Content-Type</c> its type.
    /// </summary>
    /// <param name="rtId">Runtime id of the target folder or root</param>
    /// <param name="name">File name</param>
    /// <param name="conflict">fail (409 when the name exists), replace (new content for the existing file), keepBoth ("a (1).pdf")</param>
    [HttpPost("items/{rtId}/content")]
    [Authorize(AuthenticationSchemes = InfrastructureCommon.OidcAuthenticationScheme,
        Policy = AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    [ProducesResponseType(typeof(FileEntryDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(FileEntryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge)]
    public Task<IActionResult> UploadIntoFolderById(string rtId, [FromQuery] string name,
        [FromQuery] string? conflict = null)
    {
        return ExecuteAsync(async (repository, session, unfiltered) =>
        {
            var mode = ParseConflict(conflict);
            var folder = await FindByIdAsync(repository, session, rtId);
            if (!folder.IsContainer)
            {
                throw FileSystemException.NotAFolder(folder.Name);
            }

            return await UploadAsync(repository, session, unfiltered, folder, name, mode, null, null);
        });
    }

    /// <summary>
    ///     Replaces the content of the file addressed by rtId (rtId and links stay).
    /// </summary>
    [HttpPut("items/{rtId}/content")]
    [Authorize(AuthenticationSchemes = InfrastructureCommon.OidcAuthenticationScheme,
        Policy = AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    [ProducesResponseType(typeof(FileEntryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge)]
    public Task<IActionResult> ReplaceById(string rtId)
    {
        return ExecuteAsync(async (repository, session, unfiltered) =>
        {
            var file = await FindByIdAsync(repository, session, rtId);
            if (file.Kind != FileSystemEntryKind.File)
            {
                throw FileSystemException.NotAFile(file.Name);
            }

            // Addressed by rtId: no name resolution, so legacy duplicates and legacy names stay replaceable.
            await using var content = await BufferRequestBodyAsync();
            session.StartTransaction();
            var replaced = await fileSystem.ReplaceContentAsync(repository, session, unfiltered, file,
                ResolveContentType(file.Name), content);
            await session.CommitTransactionAsync();
            return Ok(FileSystemService.ToDto(replaced, replaced: true));
        });
    }

    /// <summary>
    ///     Downloads a file addressed by path: <c>{root}/{folders…}/{name}</c>, every segment URL-encoded.
    /// </summary>
    /// <param name="root">Well-known name of the folder root</param>
    /// <param name="path">Path of the file below the root</param>
    /// <param name="inline">Ask for inline delivery (preview); active content (SVG, HTML, …) stays an attachment</param>
    [HttpGet("{root}/{**path}")]
    [Authorize(AuthenticationSchemes = InfrastructureCommon.OidcAuthenticationScheme,
        Policy = AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public Task<IActionResult> DownloadByPath(string root, string? path, [FromQuery] bool inline = false)
    {
        return ExecuteAsync(async (repository, session, _) =>
        {
            var file = await fileSystem.ResolveAsync(repository, session, root, path);
            return await DownloadAsync(repository, session, file, inline);
        });
    }

    /// <summary>
    ///     Uploads a file by path: <c>{root}/{folders…}/{name}</c>, every segment URL-encoded. The request body
    ///     is the raw content, <c>Content-Type</c> its type. Answers 201 with the stored file, or 200 when
    ///     <c>conflict=replace</c> replaced an existing file.
    /// </summary>
    /// <param name="root">Well-known name of the folder root</param>
    /// <param name="path">Path of the file below the root (folders + file name)</param>
    /// <param name="conflict">fail (default, 409 when the name exists), replace, keepBoth</param>
    /// <param name="createFolders">Create missing folders on the way (default false: 404 PATH_NOT_FOUND)</param>
    [HttpPut("{root}/{**path}")]
    [Authorize(AuthenticationSchemes = InfrastructureCommon.OidcAuthenticationScheme,
        Policy = AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    [ProducesResponseType(typeof(FileEntryDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(FileEntryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge)]
    public Task<IActionResult> UploadByPath(string root, string? path, [FromQuery] string? conflict = null,
        [FromQuery] bool createFolders = false)
    {
        return ExecuteAsync(async (repository, session, unfiltered) =>
        {
            var mode = ParseConflict(conflict);
            var segments = FileSystemNames.SplitPath(path);
            if (segments.Count == 0)
            {
                throw FileSystemException.InvalidRequest("The path must end with the file name.");
            }

            var folderPath = FileSystemNames.JoinPath(segments.Take(segments.Count - 1));
            FileSystemNames.Validate(segments[^1]);

            // Buffer (and enforce the limit) first, then create missing folders and the file in one
            // transaction, so a refused upload leaves no empty folders behind.
            await using var content = await BufferRequestBodyAsync();
            session.StartTransaction();
            var folder = await fileSystem.ResolveFolderAsync(repository, session, unfiltered, root, folderPath,
                createFolders);
            return await StoreAsync(repository, session, unfiltered, folder, segments[^1], mode, root, folderPath,
                content);
        });
    }

    /// <summary>
    ///     Streamed zip of files and folders (AB#6225), addressed by path (<c>items</c>) and/or rtId
    ///     (<c>rtIds</c>); folders are included recursively. Limits (ZipMaxFiles, ZipMaxBytes = sum of the
    ///     source sizes) are checked before the first byte (413 LIMIT_EXCEEDED); an entry that does not exist
    ///     or is hidden by data permissions answers 404 before streaming; hidden entries below a selected
    ///     folder are left out silently.
    /// </summary>
    [HttpPost("zip")]
    [Authorize(AuthenticationSchemes = InfrastructureCommon.OidcAuthenticationScheme,
        Policy = AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge)]
    public Task<IActionResult> DownloadZip([FromBody] ZipRequestDto request)
    {
        return ExecuteAsync(async (repository, session, _) =>
        {
            var refs = request.Items ?? [];
            var rtIds = request.RtIds ?? [];
            if (refs.Count + rtIds.Count == 0)
            {
                throw FileSystemException.InvalidRequest("Select at least one file or folder (items or rtIds).");
            }

            if (refs.Count + rtIds.Count > Limits.ZipMaxFiles)
            {
                throw FileSystemException.ZipTooManyFiles(refs.Count + rtIds.Count, Limits.ZipMaxFiles);
            }

            var selection = new List<FileSystemEntry>();
            var seen = new HashSet<string>();
            foreach (var item in refs)
            {
                var entry = await fileSystem.ResolveAsync(repository, session, item.Root, item.Path);
                if (seen.Add(entry.Id.RtId.ToString()))
                {
                    selection.Add(entry);
                }
            }

            foreach (var rtId in rtIds)
            {
                var entry = await FindByIdAsync(repository, session, rtId);
                if (seen.Add(entry.Id.RtId.ToString()))
                {
                    selection.Add(entry);
                }
            }

            var plan = await zipService.PlanAsync(repository, session, selection, Limits, HttpContext.RequestAborted);

            var fileName = string.IsNullOrWhiteSpace(request.FileName) ? "files.zip" : request.FileName.Trim();
            if (!fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                fileName += ".zip";
            }

            Response.StatusCode = StatusCodes.Status200OK;
            Response.ContentType = "application/zip";
            FileResponseHeaders.Apply(Response, fileName, "application/zip", inline: false);
            HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            // ZipArchive writes the small local-header/data-descriptor records synchronously when an entry
            // stream is disposed (also with DisposeAsync, .NET 10, non-seekable output). Kestrel forbids
            // synchronous IO by default, which aborted every zip after its first entry; allow it for this
            // response only. File contents are still copied asynchronously.
            var bodyControl = HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpBodyControlFeature>();
            if (bodyControl != null)
            {
                bodyControl.AllowSynchronousIO = true;
            }

            try
            {
                await zipService.WriteAsync(repository, session, plan, Response.Body, HttpContext.RequestAborted);
            }
            catch (Exception e) when (Response.HasStarted)
            {
                // The status line is gone: abort so the client sees a broken download, not a truncated zip.
                logger.LogError(e, "Zip download aborted after the response started");
                HttpContext.Abort();
            }

            return new EmptyResult();
        });
    }

    // ---------------------------------------------------------------------------------------------

    private async Task<IActionResult> UploadAsync(ITenantRepository repository, IOctoSession session,
        IOctoSession unfiltered, FileSystemEntry folder, string name, FileConflictMode mode, string? root,
        string? folderPath)
    {
        FileSystemNames.Validate(name);
        await using var content = await BufferRequestBodyAsync();
        session.StartTransaction();
        return await StoreAsync(repository, session, unfiltered, folder, name, mode, root, folderPath, content);
    }

    private async Task<IActionResult> StoreAsync(ITenantRepository repository, IOctoSession session,
        IOctoSession unfiltered, FileSystemEntry folder, string name, FileConflictMode mode, string? root,
        string? folderPath, Stream content)
    {
        var (entry, replaced) = await fileSystem.UploadAsync(repository, session, unfiltered, folder, name,
            ResolveContentType(name), content, mode);
        await session.CommitTransactionAsync();

        var dto = FileSystemService.ToDto(entry, root,
            root == null ? null : FileSystemNames.JoinPath(FileSystemNames.SplitPath(folderPath).Append(entry.Name)),
            folder.Id, replaced);
        return replaced ? Ok(dto) : StatusCode(StatusCodes.Status201Created, dto);
    }

    private async Task<IActionResult> DownloadAsync(ITenantRepository repository, IOctoSession session,
        FileSystemEntry file, bool inline)
    {
        if (file.Kind != FileSystemEntryKind.File)
        {
            throw FileSystemException.NotAFile(file.Name);
        }

        var content = file.Content;
        if (content?.BinaryId == null)
        {
            throw FileSystemException.PathNotFound(file.Name);
        }

        Runtime.Contracts.Repositories.IDownloadStreamHandler download;
        try
        {
            download = await repository.DownloadLargeBinaryAsync(session, content.BinaryId.Value);
        }
        catch (Runtime.Contracts.MongoDb.EntityNotFoundException)
        {
            throw FileSystemException.PathNotFound(file.Name); // entity without bytes (orphaned GridFS reference)
        }

        if (download.Stream == null)
        {
            throw FileSystemException.PathNotFound(file.Name);
        }

        var contentType = BinaryContentTypeDetector.IsGenericOrEmpty(content.ContentType)
            ? ResolveContentType(file.Name)
            : content.ContentType;
        FileResponseHeaders.Apply(Response, file.Name, contentType, inline, content.BinaryId.Value.ToString());
        return new FileStreamResult(download.Stream, contentType);
    }

    /// <summary>
    ///     Copies the request body into a temporary file (the engine needs a seekable stream with a length)
    ///     while enforcing <see cref="FilesOptions.MaxUploadBytes" /> — before reading for a declared length,
    ///     while reading for chunked bodies.
    /// </summary>
    private async Task<FileStream> BufferRequestBodyAsync()
    {
        var max = Limits.MaxUploadBytes;
        if (Request.ContentLength > max)
        {
            throw FileSystemException.UploadTooLarge(max);
        }

        var sizeFeature = HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = max + 1;
        }

        var temp = new FileStream(Path.Combine(Path.GetTempPath(), $"octo-upload-{Guid.NewGuid():N}"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await Request.Body.ReadAsync(buffer, HttpContext.RequestAborted)) > 0)
            {
                total += read;
                if (total > max)
                {
                    throw FileSystemException.UploadTooLarge(max);
                }

                await temp.WriteAsync(buffer.AsMemory(0, read), HttpContext.RequestAborted);
            }

            temp.Position = 0;
            return temp;
        }
        catch
        {
            await temp.DisposeAsync();
            throw;
        }
    }

    private string ResolveContentType(string name)
    {
        var declared = Request.ContentType?.Split(';')[0].Trim();
        if (!BinaryContentTypeDetector.IsGenericOrEmpty(declared) &&
            !string.Equals(declared, "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            return declared!;
        }

        return ContentTypeProvider.TryGetContentType(name, out var byExtension)
            ? byExtension
            : BinaryContentTypeDetector.GenericContentType;
    }

    private async Task<FileSystemEntry> FindByIdAsync(ITenantRepository repository, IOctoSession session,
        string rtId)
    {
        if (!Meshmakers.Octo.ConstructionKit.Contracts.OctoObjectId.TryParse(rtId, out var id))
        {
            throw FileSystemException.InvalidRequest($"'{rtId}' is not a valid rtId.");
        }

        return await fileSystem.FindByRtIdAsync(repository, session, id)
               ?? throw FileSystemException.ItemNotFound(rtId);
    }

    private static FileConflictMode ParseConflict(string? conflict)
    {
        return conflict?.ToLowerInvariant() switch
        {
            null or "" or "fail" => FileConflictMode.Fail,
            "replace" => FileConflictMode.Replace,
            "keepboth" => FileConflictMode.KeepBoth,
            _ => throw FileSystemException.InvalidRequest(
                $"Unknown conflict mode '{conflict}' (fail, replace, keepBoth).")
        };
    }

    /// <summary>
    ///     Resolves the tenant, opens the caller's session (data permissions) and an unfiltered session (name
    ///     conflicts, parents) and maps file system errors to problem details.
    /// </summary>
    private async Task<IActionResult> ExecuteAsync(
        Func<ITenantRepository, IOctoSession, IOctoSession, Task<IActionResult>> action)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return Problem(FileSystemException.InvalidRequest("The tenant id is missing."));
        }

        try
        {
            var repository = await octoService.SystemContext.FindTenantRepositoryAsync(tenantId);
            using var session = repository.GetSession(GraphQL.Helpers.GetSecurityContext(HttpContext.User));
            using var unfiltered = repository.GetSession();
            return await action(repository, session, unfiltered);
        }
        catch (FileSystemException e)
        {
            return Problem(e);
        }
        catch (Exception e) when (FileSystemService.IsDataPermissionDenial(e))
        {
            return Problem(FileSystemException.Forbidden("Access denied by data permissions."));
        }
        catch (Runtime.Contracts.MongoDb.EntityNotFoundException)
        {
            return Problem(FileSystemException.PathNotFound("entry"));
        }
        catch (Runtime.Contracts.PersistenceException)
        {
            // Write conflicts and other engine failures: retryable, no engine internals in the answer.
            return Problem(new FileSystemException(FileSystemErrorCodes.InvalidRequest, StatusCodes.Status409Conflict,
                "The change could not be stored (concurrent change?); reload and retry."));
        }
        catch (AssetRepositoryException e)
        {
            return Problem(new FileSystemException(FileSystemErrorCodes.InvalidRequest, StatusCodes.Status400BadRequest,
                string.Join("; ", e.Details.Select(d => d.Message).DefaultIfEmpty(e.Message))));
        }
    }

    private ObjectResult Problem(FileSystemException exception)
    {
        var problem = new ProblemDetails
        {
            Status = exception.StatusCode,
            Title = exception.Code,
            Detail = exception.Message
        };
        problem.Extensions["code"] = exception.Code;
        return new ObjectResult(problem)
        {
            StatusCode = exception.StatusCode,
            ContentTypes = { "application/problem+json" }
        };
    }
}
