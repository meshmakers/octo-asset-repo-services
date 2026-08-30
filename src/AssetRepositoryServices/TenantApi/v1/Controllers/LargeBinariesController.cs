using Asp.Versioning;
using IdentityModel;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Services.Infrastructure;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.TenantApi.v1.Controllers;

/// <summary>
///     REST Controller for tenant-specific access to large binaries
/// </summary>
[Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
[Route("{tenantId:tenantId}/v{version:apiVersion}/[controller]")]
[ApiController]
[ApiVersion("1.0")]
// ReSharper disable once ClassNeverInstantiated.Global
public class LargeBinariesController : ControllerBase
{
    private const int SniffBufferSize = 16;

    private readonly IOctoService _octoService;
    private readonly Runtime.Contracts.DataPermissions.IDataPermissionResolver _dataPermissionResolver;
    private readonly ConstructionKit.Contracts.Services.ICkCacheService _ckCacheService;

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="octoService">Octo service for tenant management</param>
    /// <param name="dataPermissionResolver">Resolver of the tenant's data-policy table (AB#4985)</param>
    /// <param name="ckCacheService">Construction kit cache</param>
    public LargeBinariesController(IOctoService octoService,
        Runtime.Contracts.DataPermissions.IDataPermissionResolver dataPermissionResolver,
        ConstructionKit.Contracts.Services.ICkCacheService ckCacheService)
    {
        _octoService = octoService;
        _dataPermissionResolver = dataPermissionResolver;
        _ckCacheService = ckCacheService;
    }

    // GET {tenantId}/v1/largeBinaries
    /// <summary>
    ///     Downloads are large binary with given tenantId and large binary id
    /// </summary>
    /// <returns></returns>
    [HttpGet]
    // AB#4973: previously bearer-auth only — no scope requirement at all on a direct binary download.
    // The read-only scope policy is the minimum; the per-entity check is the AB#4985 gate below.
    [Authorize(AuthenticationSchemes = InfrastructureCommon.OidcAuthenticationScheme,
        Policy = AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Get([FromQuery] string largeBinaryId)
    {
        try
        {
            var tenantId = HttpContext.GetTenantId();
            if (string.IsNullOrEmpty(tenantId))
            {
                return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty" });
            }

            if (string.IsNullOrEmpty(largeBinaryId))
            {
                return BadRequest(new InternalServerErrorDto("LargeBinaryId is required"));
            }

            var tenantRepository = await _octoService.SystemContext.FindTenantRepositoryAsync(tenantId);

            using var session = await tenantRepository.GetSessionAsync().ConfigureAwait(false);
            session.StartTransaction();

            // AB#4985 (K1): a binary download is gated by the data permissions of its owning entity —
            // the entity id is stamped on the binary at upload time. Denied/foreign-owned answers 404
            // (never 403) so the gate leaks no existence, mirroring the read filter. Binaries without
            // an owner (temporary uploads, legacy data) and unprotected owner types stay open, and
            // tenants without enforcing policies take the pre-permission fast path untouched.
            var gateResult = await EnsureBinaryVisibleAsync(tenantRepository, session, tenantId,
                OctoObjectId.Parse(largeBinaryId)).ConfigureAwait(false);
            if (gateResult != null)
            {
                return gateResult;
            }

            var streamHandler = await tenantRepository.DownloadLargeBinaryAsync(session, OctoObjectId.Parse(largeBinaryId));
            if (streamHandler.Stream == null)
            {
                return NotFound(new ErrorResponse { ErrorMessage = "Large binary not found" });
            }

            await session.CommitTransactionAsync().ConfigureAwait(false);

            // Self-heal old uploads that were stored before content-type detection existed
            // (or under a code path that did not set a specific type). Sniff the magic
            // bytes from the head of the stream so the response carries a useful MIME type
            // — important for callers like <link rel="icon">, where browsers reject
            // application/octet-stream as a favicon.
            var (contentType, responseStream) = await EnsureSpecificContentTypeAsync(
                streamHandler.Stream,
                streamHandler.ContentType);

            return new FileStreamResult(responseStream, contentType);
        }
        catch (Runtime.Contracts.MongoDb.EntityNotFoundException)
        {
            // A missing binary id used to fall into the generic 500 catch — a not-found is a 404.
            return NotFound(new ErrorResponse { ErrorMessage = "Large binary not found" });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new InternalServerErrorDto(ex.Message));
        }
        catch (FormatException ex)
        {
            return BadRequest(new InternalServerErrorDto($"Invalid largeBinaryId format: {ex.Message}"));
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    /// <summary>
    ///     Data-permission gate for a binary download (AB#4985). Returns null when the download may
    ///     proceed, or the action result (404) to answer instead. Classification mirrors the read
    ///     filter: Open/Allowed owner type &#8594; allow; Denied &#8594; 404; OwnedOnly &#8594; the stored creator of
    ///     the owning entity must be the caller (raw entity read — the filtered path would hide the
    ///     foreign owner and make the check pass vacuously).
    /// </summary>
    private async Task<IActionResult?> EnsureBinaryVisibleAsync(
        Runtime.Contracts.MongoDb.Repositories.ITenantRepository tenantRepository,
        Runtime.Contracts.IOctoSession session, string tenantId, OctoObjectId largeBinaryId)
    {
        var securityContext = GraphQL.Helpers.GetSecurityContext(HttpContext.User);
        if (securityContext.IsSystem)
        {
            return null;
        }

        var policyTable = await _dataPermissionResolver.GetPolicyTableAsync(tenantRepository).ConfigureAwait(false);
        if (!policyTable.HasRules)
        {
            return null;
        }

        var binaryInfo = await tenantRepository.GetLargeBinaryInfoAsync(session, largeBinaryId).ConfigureAwait(false);
        if (binaryInfo?.RtEntityId == null)
        {
            // Missing binary answers downstream; ownerless binaries (temporary/legacy) stay open.
            return null;
        }

        var ownerEntityId = binaryInfo.RtEntityId.Value;
        var selfAndBase = Runtime.Contracts.DataPermissions.RtDataPermissionCkTypeHelper.GetSelfAndBaseFullNames(
            _ckCacheService, tenantId, ownerEntityId.CkTypeId);
        var level = Runtime.Contracts.DataPermissions.RtDataAccessEvaluator.Classify(policyTable, selfAndBase,
            Runtime.Contracts.DataPermissions.RtDataAction.Read, securityContext,
            includeAuditOnlyPolicies: false);

        switch (level)
        {
            case Runtime.Contracts.DataPermissions.RtDataAccessLevel.Denied:
                return NotFound(new ErrorResponse { ErrorMessage = "Large binary not found" });
            case Runtime.Contracts.DataPermissions.RtDataAccessLevel.OwnedOnly:
                var owner = await tenantRepository.GetRtEntityByRtIdAsync(session, ownerEntityId)
                    .ConfigureAwait(false);
                // A dangling owner reference protects nothing — treat like an ownerless binary.
                if (owner != null)
                {
                    // AB#4978: a CK-model-declared owner attribute path replaces the stamped creator.
                    var ownerAttributePath = Runtime.Contracts.DataPermissions.RtDataPermissionCkTypeHelper
                        .GetEffectiveOwnerAttributePath(_ckCacheService, tenantId, ownerEntityId.CkTypeId);
                    var ownerSubject = ownerAttributePath == null
                        ? owner.RtCreatedBy
                        : owner.GetAttributeValueByAccessPath(_ckCacheService, tenantId, ownerAttributePath)
                            as string;
                    if (ownerSubject != securityContext.SubjectId)
                    {
                        return NotFound(new ErrorResponse { ErrorMessage = "Large binary not found" });
                    }
                }

                return null;
            default:
                return null;
        }
    }

    private static async Task<(string ContentType, Stream Stream)> EnsureSpecificContentTypeAsync(
        Stream stream,
        string? storedContentType)
    {
        if (!BinaryContentTypeDetector.IsGenericOrEmpty(storedContentType))
        {
            return (storedContentType!, stream);
        }

        var buffer = new byte[SniffBufferSize];
        var bytesRead = await ReadUpToAsync(stream, buffer, SniffBufferSize).ConfigureAwait(false);
        var detected = BinaryContentTypeDetector.Detect(buffer.AsSpan(0, bytesRead));

        // Whatever we choose for the response body, the consumer must still see the
        // bytes we already pulled off the source stream. Reset if possible, else
        // prepend the read bytes back onto a wrapper.
        Stream responseStream;
        if (stream.CanSeek)
        {
            stream.Position = 0;
            responseStream = stream;
        }
        else
        {
            var prefix = new byte[bytesRead];
            Array.Copy(buffer, prefix, bytesRead);
            responseStream = new PrependedReadStream(prefix, stream);
        }

        var resolvedContentType =
            BinaryContentTypeDetector.IsGenericOrEmpty(detected)
                ? storedContentType ?? BinaryContentTypeDetector.GenericContentType
                : detected;

        return (resolvedContentType, responseStream);
    }

    private static async Task<int> ReadUpToAsync(Stream stream, byte[] buffer, int count)
    {
        var total = 0;
        while (total < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, count - total)).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        return total;
    }
}
