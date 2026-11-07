using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Messages;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services.CkModelValidation;

/// <summary>
///     Validates a CK model draft against the models installed in a tenant without importing it (AB#8492).
/// </summary>
public interface ICkModelDraftValidator
{
    /// <summary>
    ///     Validates the uploaded draft: a zip of the source <c>ConstructionKit/</c> folder (compiled here with the engine's
    ///     compiler, dependencies from the configured catalogs) or a compiled model (json, yaml or a zip of one, read like
    ///     <c>ImportCk</c> reads it). The compiled model is then checked against the tenant with the import's own checks
    ///     (<see cref="ITenantContext.ValidateCkModelAsync" />) and, when it has no error, with the tenant upgrade guard.
    ///     Nothing is imported, cached, queued or written. Never throws for a bad draft: every finding is in the result.
    /// </summary>
    Task<CkModelValidationResponseDto> ValidateAsync(string tenantId, Stream upload, string fileName,
        string? contentType, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class CkModelDraftValidator(
    ICompilerService compilerService,
    ICkSerializer ckSerializer,
    ISystemContext systemContext,
    ICkModelLibraryStatusService libraryStatusService,
    ILogger<CkModelDraftValidator> logger) : ICkModelDraftValidator
{
    /// <summary>Name of the model file that marks the root of a source <c>ConstructionKit/</c> folder.</summary>
    public const string CkModelFileName = "ckModel.yaml";

    /// <summary>Most entries a source zip may have.</summary>
    public const int MaxZipEntries = 2_000;

    /// <summary>Most bytes a source zip may unpack to.</summary>
    public const long MaxUnpackedBytes = 50L * 1024 * 1024;

    /// <summary>Message number of a draft that cannot be read at all (not a zip, unsafe entry, too large, no model).</summary>
    public const int UnreadableDraftNumber = 8492;

    /// <inheritdoc />
    public async Task<CkModelValidationResponseDto> ValidateAsync(string tenantId, Stream upload, string fileName,
        string? contentType, CancellationToken cancellationToken)
    {
        var response = new CkModelValidationResponseDto();

        // The upload is read more than once (zip probe, then source or compiled), so it is buffered.
        await using var buffer = new MemoryStream();
        await upload.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;

        var isZip = CkModelFileReader.IsZip(contentType) ||
                    fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

        CkCompiledModelRoot? compiled;
        string? sourceRoot = null;
        try
        {
            if (isZip && TryFindSourceRoot(buffer, out var prefix))
            {
                response.SourceKind = CkModelValidationResponseDto.SourceKindSource;
                sourceRoot = Path.Combine(Path.GetTempPath(), $"octo-ck-validate-{Guid.NewGuid():N}");
                ExtractSource(buffer, prefix, sourceRoot);
                compiled = await CompileSourceAsync(sourceRoot, response);
            }
            else
            {
                buffer.Position = 0;
                compiled = await CkModelFileReader.ReadCompiledModelAsync(ckSerializer, buffer, fileName, isZip);
                response.SourceKind = CkModelValidationResponseDto.SourceKindCompiled;
            }
        }
        catch (InvalidOperationException e)
        {
            response.Errors.Add(Unreadable(WithoutRoot(e.Message, sourceRoot)));
            compiled = null;
        }
        catch (InvalidDataException e)
        {
            // Also thrown while extracting (corrupt entry data), so the text may name the temp folder: strip it.
            response.Errors.Add(Unreadable(
                $"The file '{fileName}' is not a readable zip archive: {WithoutRoot(e.Message, sourceRoot)}"));
            compiled = null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // e.g. a YAML syntax error in a compiled model: a finding about the draft, not a server error.
            // An I/O failure while extracting (e.g. a duplicate zip entry) names the temp path: strip it.
            response.Errors.Add(Unreadable(
                $"The file '{fileName}' cannot be read as a CK model: {WithoutRoot(e.Message, sourceRoot)}"));
            compiled = null;
        }
        finally
        {
            if (sourceRoot != null)
            {
                TryDelete(sourceRoot);
            }
        }

        if (compiled != null)
        {
            response.ModelId = compiled.ModelId.FullName;

            // AB#8492: the tenant half - the same checks the import runs before it writes, read-only.
            var tenantContext = await systemContext.FindTenantContextAsync(tenantId);
            var tenantResult = await tenantContext.ValidateCkModelAsync(compiled);
            AddMessages(response, tenantResult, sourceRoot: null);

            if (response.Errors.Count == 0)
            {
                // Same guard the import applies (AB#6966): which installed models the draft would break.
                var blockers = await libraryStatusService.EvaluateFileImportBlockersAsync(tenantId, [compiled],
                    cancellationToken);
                response.Blockers = blockers.ToList();
                response.IsBlocked = response.Blockers.Count > 0;
            }
        }

        response.IsValid = response.Errors.Count == 0;
        logger.LogInformation(
            "CK model draft '{FileName}' ({SourceKind}, model {ModelId}) validated for tenant {TenantId}: {ErrorCount} error(s), {WarningCount} warning(s), {BlockerCount} blocker(s)",
            fileName, response.SourceKind ?? "unreadable", response.ModelId ?? "-", tenantId, response.Errors.Count,
            response.Warnings.Count, response.Blockers.Count);
        return response;
    }

    private async Task<CkCompiledModelRoot?> CompileSourceAsync(string sourceRoot, CkModelValidationResponseDto response)
    {
        var operationResult = new OperationResult();
        CkCompiledModelRoot? compiled = null;
        try
        {
            compiled = await compilerService.CompileInMemoryAsync(sourceRoot, operationResult);
        }
        catch (CompilerException e)
        {
            // The messages are in the operation result; the exception only says that there were errors.
            if (!operationResult.HasErrors && !operationResult.HasFatalErrors)
            {
                operationResult.AddMessage(new OperationMessage(MessageLevel.Error, null, UnreadableDraftNumber,
                    e.Message, new Dictionary<string, string> { ["reason"] = ReasonUnreadableDraft }));
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A draft that the compiler cannot even parse (YAML syntax, wrong shape) is a finding, not a server error.
            // An unknown dependency is reported by the resolver as message 1 and then thrown: that is a dependency
            // finding, not an unreadable draft.
            var reason = IsDependencyResolutionFailure(e, operationResult)
                ? ReasonDependencyResolution
                : ReasonUnreadableDraft;
            operationResult.AddMessage(new OperationMessage(MessageLevel.Error, null, UnreadableDraftNumber,
                StripRoot(e.Message, sourceRoot),
                new Dictionary<string, string> { ["reason"] = reason }));
        }

        AddMessages(response, operationResult, sourceRoot);
        return operationResult.HasErrors || operationResult.HasFatalErrors ? null : compiled;
    }

    /// <summary>
    ///     The compiler's dependency resolver adds message <see cref="UnknownCkModelNumber" /> for every unknown dependency and
    ///     then throws a <see cref="ModelValidationException" /> that lists them.
    /// </summary>
    internal static bool IsDependencyResolutionFailure(Exception exception, OperationResult operationResult) =>
        exception is ModelValidationException &&
        operationResult.Messages.Any(m => m.MessageNumber == UnknownCkModelNumber);

    /// <summary>The folder inside the zip that holds <c>ckModel.yaml</c> (shallowest one), as an entry prefix.</summary>
    internal static bool TryFindSourceRoot(Stream zip, out string prefix)
    {
        zip.Position = 0;
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
        var model = archive.Entries
            .Where(e => string.Equals(e.Name, CkModelFileName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName.Count(c => c is '/' or '\\'))
            .FirstOrDefault();
        prefix = model == null ? string.Empty : model.FullName[..^model.Name.Length];
        return model != null;
    }

    /// <summary>
    ///     Extracts the entries below <paramref name="prefix" /> into <paramref name="targetRoot" />. Refuses absolute
    ///     paths, <c>..</c> segments, more than <see cref="MaxZipEntries" /> entries and more than
    ///     <see cref="MaxUnpackedBytes" /> bytes, so a draft can neither write outside the folder nor fill the disk.
    /// </summary>
    internal static void ExtractSource(Stream zip, string prefix, string targetRoot)
    {
        zip.Position = 0;
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > MaxZipEntries)
        {
            throw new InvalidOperationException(
                $"The zip archive has {archive.Entries.Count} entries; at most {MaxZipEntries} are allowed.");
        }

        var root = Path.GetFullPath(targetRoot) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (!name.StartsWith(prefix.Replace('\\', '/'), StringComparison.Ordinal) || string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var relative = name[prefix.Length..];
            if (Path.IsPathRooted(relative) || relative.Split('/').Any(s => s == ".."))
            {
                throw new InvalidOperationException($"The zip entry '{entry.FullName}' is not allowed (path outside the folder).");
            }

            var target = Path.GetFullPath(Path.Combine(root, relative));
            if (!target.StartsWith(root, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"The zip entry '{entry.FullName}' is not allowed (path outside the folder).");
            }

            total += entry.Length;
            if (total > MaxUnpackedBytes)
            {
                throw new InvalidOperationException(
                    $"The CK model folder unpacks to more than {MaxUnpackedBytes / (1024 * 1024)} MB.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
        }
    }

    private static void AddMessages(CkModelValidationResponseDto response, OperationResult result, string? sourceRoot)
    {
        foreach (var message in result.Messages)
        {
            var dto = ToDto(message, sourceRoot);
            var isError = message.MessageLevel is MessageLevel.Error or MessageLevel.FatalError;
            var list = isError ? response.Errors : response.Warnings;
            if (!list.Any(m => m.MessageNumber == dto.MessageNumber && m.Message == dto.Message && m.File == dto.File))
            {
                list.Add(dto);
            }
        }
    }

    internal static CkModelValidationMessageDto ToDto(OperationMessage message, string? sourceRoot) =>
        new()
        {
            MessageNumber = message.MessageNumber,
            Level = message.MessageLevel.ToString(),
            File = RelativeFile(message.Location, sourceRoot),
            ElementPath = ElementPath(message.Parameters),
            Message = sourceRoot == null ? message.MessageText : StripRoot(message.MessageText, sourceRoot),
            Parameters = message.Parameters.ToDictionary(p => p.Key,
                p => sourceRoot == null ? p.Value : RelativeParameter(p.Value, sourceRoot))
        };

    /// <summary>
    ///     A placeholder value without the server's temp folder: a path (e.g. <c>file</c> of message 110) becomes
    ///     relative to the draft's <c>ConstructionKit/</c> folder, any other text loses the temp root.
    /// </summary>
    internal static string RelativeParameter(string value, string sourceRoot)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return Path.IsPathRooted(value)
            ? RelativeFile(value, sourceRoot) ?? value
            : StripRoot(value, sourceRoot);
    }

    /// <summary>The draft-relative file of a message location; a server path never leaves the service.</summary>
    internal static string? RelativeFile(string? location, string? sourceRoot)
    {
        if (string.IsNullOrWhiteSpace(location) || location == "-")
        {
            return null;
        }

        if (sourceRoot == null)
        {
            return Path.IsPathRooted(location) ? Path.GetFileName(location) : location;
        }

        var full = Path.GetFullPath(sourceRoot);
        var normalized = location.Replace('\\', '/').TrimEnd('/');
        var root = full.Replace('\\', '/').TrimEnd('/');
        if (normalized == root)
        {
            // The draft folder itself (e.g. message 1, unknown dependency): no file, and never the temp folder name.
            return null;
        }

        if (normalized.StartsWith(root + "/", StringComparison.Ordinal))
        {
            return normalized[(root.Length + 1)..];
        }

        // The same folder under another spelling (e.g. /private/var vs. /var on macOS): cut at the temp folder segment.
        var segment = "/" + Path.GetFileName(root);
        var index = normalized.IndexOf(segment, StringComparison.Ordinal);
        if (index >= 0)
        {
            var rest = normalized[(index + segment.Length)..];
            if (rest.Length == 0)
            {
                return null;
            }

            if (rest[0] == '/')
            {
                return rest[1..];
            }
        }

        return Path.IsPathRooted(location) ? Path.GetFileName(location) : location;
    }

    private static string WithoutRoot(string text, string? sourceRoot) =>
        sourceRoot == null ? text : StripRoot(text, sourceRoot);

    /// <summary>
    ///     The text without the server's temp folder: a path inside the draft becomes relative to it, the folder itself
    ///     becomes <c>.</c>. Matches every spelling of the folder (e.g. <c>/private/var</c> vs. <c>/var</c> on macOS) by its
    ///     unique folder name, so neither the server path nor the folder name leaves the service.
    /// </summary>
    internal static string StripRoot(string text, string sourceRoot)
    {
        var folderName = Path.GetFileName(Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar, '/'));
        if (string.IsNullOrEmpty(folderName) || !text.Contains(folderName, StringComparison.Ordinal))
        {
            return text;
        }

        return Regex.Replace(text, $@"[^\s'""]*{Regex.Escape(folderName)}(?<sep>[/\\])?",
            m => m.Groups["sep"].Success ? string.Empty : ".");
    }

    private static readonly string[] ContainerKeys =
        ["ckTypeId", "ckRecordId", "ckEnumId", "ckInterfaceId", "ckElementId", "element", "originCkTypeId", "modelId"];

    private static readonly string[] MemberKeys =
        ["ckAttributeId", "attributeName", "attributePath", "roleId", "ckRoleId", "ckAssociationId", "methodId"];

    /// <summary>
    ///     The CK element a message is about, from its placeholder values: the container (type, record, enum, interface,
    ///     model) and, when present, the member (attribute, role, method) - e.g. <c>MyModel/Pump-1.FlowRate</c>.
    /// </summary>
    internal static string? ElementPath(IReadOnlyDictionary<string, string> parameters)
    {
        var container = ContainerKeys.Select(k => parameters.GetValueOrDefault(k)).FirstOrDefault(v => !string.IsNullOrEmpty(v));
        var member = MemberKeys.Select(k => parameters.GetValueOrDefault(k)).FirstOrDefault(v => !string.IsNullOrEmpty(v));
        return (container, member) switch
        {
            (null, null) => null,
            (null, _) => member,
            (_, null) => container,
            _ => string.Create(CultureInfo.InvariantCulture, $"{container}.{member}")
        };
    }

    /// <summary>The <c>reason</c> parameter of a draft that cannot be read.</summary>
    public const string ReasonUnreadableDraft = "unreadable-draft";

    /// <summary>The <c>reason</c> parameter of a draft whose dependencies cannot be resolved (same value as the engine's).</summary>
    public const string ReasonDependencyResolution = "dependency-resolution";

    /// <summary>Number of UnknownCkModel in the construction kit engine message catalog.</summary>
    public const int UnknownCkModelNumber = 1;

    private static CkModelValidationMessageDto Unreadable(string text) =>
        new()
        {
            MessageNumber = UnreadableDraftNumber, Level = nameof(MessageLevel.Error), Message = text,
            Parameters = new Dictionary<string, string> { ["reason"] = ReasonUnreadableDraft }
        };

    private void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Temporary CK model draft folder could not be deleted: {Reason}", e.GetType().Name);
        }
    }
}
