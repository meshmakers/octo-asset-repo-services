using System.ComponentModel.DataAnnotations;
using System.IO.Compression;
using Asp.Versioning;
using Duende.IdentityModel;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.Backend.AssetRepositoryServices.UpgradeGuard;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;
using CkCompiledModelRoot = Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects.CkCompiledModelRoot;
using CkModelDependencyDto = Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects.CkModelDependencyDto;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.CkModelMigrations;
using Meshmakers.Octo.Runtime.Contracts.Exchange;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.TenantApi.v1.Controllers;

/// <summary>
///     REST Controller for CK and RT model management
/// </summary>
[Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
[Route("{tenantId:tenantId}/v{version:apiVersion}/[controller]")]
[ApiController]
[ApiVersion("1.0")]
public class ModelsController : ControllerBase
{
    private readonly ICatalogService _catalogService;
    private readonly IAuthorizationService _authorizationService;
    private readonly ICkJsonSerializer _ckJsonSerializer;
    private readonly ICkSerializer _ckSerializer;
    private readonly IDistributedCacheService _distributedCache;
    private readonly ICommandClient<ExportRtByQueryCommandRequest> _exportRtByQueryCommandClient;
    private readonly ICommandClient<ExportRtByDeepGraphCommandRequest> _exportRtByDeepGraphCommandClient;
    private readonly ICommandClient<ImportCkCommandRequest> _importCkCommandClient;
    private readonly ICommandClient<ImportCkBatchCommandRequest> _importCkBatchCommandClient;
    private readonly ICommandClient<ImportRtCommandRequest> _importRtCommandClient;
    private readonly ICkModelLibraryStatusService _libraryStatusService;
    private readonly ILogger<ModelsController> _logger;
    private readonly ICkModelMigrationService _migrationService;
    private readonly ISystemContext _systemContext;
    private readonly ICkModelUpgradeService _upgradeService;

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="distributedCache">Instance of distributed cache</param>
    /// <param name="exportRtByQueryCommandClient"></param>
    /// <param name="exportRtByDeepGraphCommandClient"></param>
    /// <param name="importRtCommandClient"></param>
    /// <param name="importCkCommandClient"></param>
    /// <param name="importCkBatchCommandClient"></param>
    /// <param name="catalogService">CK model catalog service</param>
    /// <param name="ckJsonSerializer">CK model JSON serializer</param>
    /// <param name="systemContext">System context for tenant access</param>
    /// <param name="upgradeService">CK model upgrade service for pre-flight checks</param>
    /// <param name="migrationService">CK model migration service for migration history</param>
    /// <param name="libraryStatusService">CK model library status service (AB#5432)</param>
    /// <param name="logger">Logger (audit line of a forced breaking update, AB#6870)</param>
    /// <param name="ckSerializer">Reads the uploaded CK model of ImportCk for the upgrade guard (AB#6966)</param>
    /// <param name="authorizationService">Checks the data model management policy of a forced import (AB#6966)</param>
    public ModelsController(IDistributedCacheService distributedCache,
        ICommandClient<ExportRtByQueryCommandRequest> exportRtByQueryCommandClient,
        ICommandClient<ExportRtByDeepGraphCommandRequest> exportRtByDeepGraphCommandClient,
        ICommandClient<ImportRtCommandRequest> importRtCommandClient,
        ICommandClient<ImportCkCommandRequest> importCkCommandClient,
        ICommandClient<ImportCkBatchCommandRequest> importCkBatchCommandClient,
        ICatalogService catalogService,
        ICkJsonSerializer ckJsonSerializer,
        ISystemContext systemContext,
        ICkModelUpgradeService upgradeService,
        ICkModelMigrationService migrationService,
        ICkModelLibraryStatusService libraryStatusService,
        ILogger<ModelsController> logger,
        ICkSerializer ckSerializer,
        IAuthorizationService authorizationService)
    {
        _ckSerializer = ckSerializer;
        _authorizationService = authorizationService;
        _distributedCache = distributedCache;
        _exportRtByQueryCommandClient = exportRtByQueryCommandClient;
        _exportRtByDeepGraphCommandClient = exportRtByDeepGraphCommandClient;
        _importRtCommandClient = importRtCommandClient;
        _importCkCommandClient = importCkCommandClient;
        _importCkBatchCommandClient = importCkBatchCommandClient;
        _catalogService = catalogService;
        _ckJsonSerializer = ckJsonSerializer;
        _systemContext = systemContext;
        _upgradeService = upgradeService;
        _migrationService = migrationService;
        _libraryStatusService = libraryStatusService;
        _logger = logger;
    }

    // POST: {tenantId}/v1/Models/ExportRtByQuery
    /// <summary>
    ///     Exports a runtime model by query. The export job runs with the identity and roles of the caller, so
    ///     the file contains only the rows the caller may read on the normal API (AB#6734); a caller without an
    ///     identity is rejected with 401.
    /// </summary>
    /// <param name="exportModelRequestByQueryDto">The query options for the export</param>
    /// <returns></returns>
    [HttpPost]
    [Route("ExportRtByQuery")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(TransferModelResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ExportRtByQueryAsync(
        [FromBody] ExportModelRequestByQueryDto exportModelRequestByQueryDto)
    {
        try
        {
            var tenantId = HttpContext.GetTenantId();
            if (string.IsNullOrEmpty(tenantId))
            {
                return BadRequest(new OperationFailedErrorDto("TenantId is required"));
            }

            // AB#6734: the export runs as a background job; it must run with the data permissions of the
            // caller. A caller without an identity is rejected here, before any job is enqueued.
            if (!TryGetExportCaller(out var caller))
            {
                return Unauthorized(new OperationFailedErrorDto(ExportCallerRequiredMessage));
            }

            var args = new ExportRtByQueryCommandRequest(tenantId, exportModelRequestByQueryDto.QueryId)
            {
                InitiatedBySubjectId = caller.SubjectId,
                InitiatedByRoleNames = caller.RoleNames,
                InitiatedByClientId = caller.ClientId
            };
            var r =
                await _exportRtByQueryCommandClient.GetResponse<JobCreatedResponse>(args);
            return Ok(new TransferModelResponseDto(r.JobId));
        }
        catch (InvalidOperationException e)
        {
            return BadRequest(new InternalServerErrorDto(e.Message));
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    // POST: {tenantId}/v1/Models/ExportRtByDeepGraph
    /// <summary>
    ///     Exports a runtime model by deep graph. The export job runs with the identity and roles of the caller:
    ///     entities the caller may not read, and the associations pointing to them, are not part of the file
    ///     (AB#6734); a caller without an identity is rejected with 401.
    /// </summary>
    /// <param name="exportModelRequestByDeepGraphDto">The deep graph options for the export</param>
    /// <returns></returns>
    [HttpPost]
    [Route("ExportRtByDeepGraph")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(TransferModelResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ExportRtByDeepGraphAsync(
        [FromBody] ExportModelRequestByDeepGraphDto exportModelRequestByDeepGraphDto)
    {
        try
        {
            var tenantId = HttpContext.GetTenantId();
            if (string.IsNullOrEmpty(tenantId))
            {
                return BadRequest(new OperationFailedErrorDto("TenantId is required"));
            }

            // AB#6734: see ExportRtByQuery — the job runs with the caller's data permissions.
            if (!TryGetExportCaller(out var caller))
            {
                return Unauthorized(new OperationFailedErrorDto(ExportCallerRequiredMessage));
            }

            var args = new ExportRtByDeepGraphCommandRequest(tenantId,
                exportModelRequestByDeepGraphDto.OriginRtIds,
                exportModelRequestByDeepGraphDto.OriginCkTypeId)
            {
                InitiatedBySubjectId = caller.SubjectId,
                InitiatedByRoleNames = caller.RoleNames,
                InitiatedByClientId = caller.ClientId,
                FollowSpecs = exportModelRequestByDeepGraphDto.FollowSpecs?
                    .Select(spec => new DeepGraphFollowSpecRequest(spec.RoleId, spec.Direction))
                    .ToList()
            };
            var r =
                await _exportRtByDeepGraphCommandClient.GetResponse<JobCreatedResponse>(args);
            return Ok(new TransferModelResponseDto(r.JobId));
        }
        catch (InvalidOperationException e)
        {
            return BadRequest(new InternalServerErrorDto(e.Message));
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    private const string ExportCallerRequiredMessage = "The caller identity is required to export runtime data";

    /// <summary>
    ///     The identity an export job runs with (AB#6734): built with the very mapping the GraphQL surface uses
    ///     (<see cref="GraphQL.Helpers.GetSecurityContext(System.Security.Claims.ClaimsPrincipal?)" />: subject from
    ///     <c>sub</c> / NameIdentifier / <c>client_id</c>, roles from the role claims), so that a client-credentials
    ///     token without <c>sub</c> is evaluated as its client with the client's roles — never as the system.
    /// </summary>
    internal sealed record ExportCaller(string SubjectId, IReadOnlyList<string> RoleNames, string? ClientId);

    /// <summary>
    ///     Resolves the caller of an export route; false (fail closed) when the principal is unauthenticated or
    ///     carries neither a subject nor a client id.
    /// </summary>
    private bool TryGetExportCaller(out ExportCaller caller)
    {
        caller = null!;
        if (HttpContext.User.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var securityContext = GraphQL.Helpers.GetSecurityContext(HttpContext.User);
        if (string.IsNullOrWhiteSpace(securityContext.SubjectId))
        {
            return false;
        }

        caller = new ExportCaller(securityContext.SubjectId, securityContext.Roles.ToList(),
            HttpContext.User.FindFirst("client_id")?.Value);
        return true;
    }

    // POST: {tenantId}/v1/Models/ImportRt
    /// <summary>
    ///     Imports a runtime model
    /// </summary>
    /// <param name="importStrategy">The import strategy to use for the import</param>
    /// <param name="file">The file with the RT model definition</param>
    /// <returns>
    ///     The job id. For CK types whose data policy opted in with <c>ProtectBlueprintLocked</c> the job fails
    ///     (nothing written) when the file would overwrite an entity that is locked by a blueprint, and strips the
    ///     blueprint attributes (<c>RtBlueprintLocked</c>, <c>RtBlueprintSource</c>, <c>RtBlueprintAppliedAt</c>)
    ///     from the file (AB#6392).
    /// </returns>
    [HttpPost]
    [RequestSizeLimit(300_000_000)]
    [Route("ImportRt")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    [ProducesResponseType(typeof(TransferModelResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ImportRt([Required] ImportStrategyDto importStrategy, [Required] IFormFile file)
    {
        try
        {
            var tenantId = HttpContext.GetTenantId();
            if (string.IsNullOrEmpty(tenantId))
            {
                return BadRequest(new OperationFailedErrorDto("TenantId is required"));
            }

            var insertStrategy = importStrategy switch
            {
                ImportStrategyDto.InsertOnly => ImportStrategy.Insert,
                ImportStrategyDto.Upsert => ImportStrategy.Upsert,
                _ => throw new ArgumentOutOfRangeException(nameof(importStrategy), importStrategy, null)
            };
            // AB#6392: the import runs as a background job, so the caller is passed along; the job applies the
            // blueprint-lock protection of opted-in CK types on their behalf. A caller without an identity is
            // rejected here, never imported as if it were the system.
            var callerSubjectId = GraphQL.Helpers.GetSecurityContext(HttpContext.User).SubjectId;
            if (string.IsNullOrWhiteSpace(callerSubjectId))
            {
                return Unauthorized(new OperationFailedErrorDto("The caller identity is required to import runtime data"));
            }

            var cacheKey = await AddFileToCache(tenantId, file);
            var args = new ImportRtCommandRequest(tenantId, insertStrategy, cacheKey)
            {
                InitiatedBySubjectId = callerSubjectId
            };
            var r =
                await _importRtCommandClient.GetResponse<JobCreatedResponse>(args);
            return Ok(new TransferModelResponseDto(r.JobId));
        }
        catch (InvalidOperationException e)
        {
            return BadRequest(new InternalServerErrorDto(e.Message));
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    // POST: {tenantId}/v1/Models/ImportCk
    /// <summary>
    ///     Imports a construction kit model
    ///     <para>
    ///         Tenant upgrade guard (CK v2 F2.5, AB#6966): the uploaded file is read and judged before anything is cached
    ///         or queued, as an explicit caller (service-managed models such as <c>System</c> are judged too, and a
    ///         downgrade like an upgrade). When it would break an installed model the request is refused with
    ///         <c>409 CK_UPGRADE_BLOCKED</c> (problem details with the structured blockers); no job is queued and
    ///         nothing is written. <c>forceBreakingUpdate=true</c> (form member) imports anyway, needs the data model
    ///         management policy (403 otherwise) and logs the blockers and the caller at WARN. A file that cannot be read
    ///         is a 400; a guard failure is a 500 even when forced (fail closed).
    ///     </para>
    /// </summary>
    /// <param name="file">The file with the CK model definition</param>
    /// <param name="forceBreakingUpdate">Import even if it breaks an installed model (default false)</param>
    /// <returns></returns>
    [HttpPost]
    [Route("ImportCk")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    [ProducesResponseType(typeof(TransferModelResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ImportCk(IFormFile file, [FromForm] bool forceBreakingUpdate = false)
    {
        try
        {
            var tenantId = HttpContext.GetTenantId();
            if (string.IsNullOrEmpty(tenantId))
            {
                return BadRequest(new OperationFailedErrorDto("TenantId is required"));
            }

            if (forceBreakingUpdate && !await MayForceBreakingUpdateAsync())
            {
                return Forbid();
            }

            // AB#6966: tenant upgrade guard - before anything is cached or queued.
            var compiled = await ReadCompiledModelAsync(file);
            var blockers = await _libraryStatusService.EvaluateFileImportBlockersAsync(tenantId, [compiled],
                HttpContext.RequestAborted);
            if (blockers.Count > 0)
            {
                var updateSet = new List<string> { compiled.ModelId.FullName };
                if (!forceBreakingUpdate)
                {
                    return CkUpgradeBlockedError.CreateFileImportResult(file.FileName, updateSet, blockers.ToList());
                }

                _logger.LogWarning(
                    "Forced breaking CK update on tenant {TenantId} by {Caller}: file import {File} of {UpdateSet} breaks {BlockerCount} installed model(s): {Blockers}",
                    tenantId, CallerDescription.From(HttpContext.User), file.FileName, string.Join(", ", updateSet),
                    blockers.Count,
                    string.Join("; ", blockers.Select(b => $"{b.Dependent} [{b.Kind} {b.Subject}] by {b.Update}")));
            }

            var cacheKey = await AddFileToCache(tenantId, file);
            var args = new ImportCkCommandRequest(tenantId, cacheKey);
            var r =
                await _importCkCommandClient.GetResponse<JobCreatedResponse>(args);
            return Ok(new TransferModelResponseDto(r.JobId));
        }
        catch (InvalidOperationException e)
        {
            return BadRequest(new InternalServerErrorDto(e.Message));
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    // POST: {tenantId}/v1/Models/ImportFromCatalog
    /// <summary>
    ///     Imports a construction kit model directly from a catalog
    ///     <para>
    ///         Tenant upgrade guard (CK v2 F2.5, AB#6870): when the model would break an installed model the request is
    ///         refused with <c>409 CK_UPGRADE_BLOCKED</c> (problem details with the structured blockers); no job is
    ///         queued and nothing is written. <c>ForceBreakingUpdate = true</c> imports anyway and logs the blockers and
    ///         the caller at WARN.
    ///     </para>
    /// </summary>
    /// <param name="request">The catalog name and model ID to import</param>
    /// <returns>A job ID for tracking the async import operation</returns>
    [HttpPost]
    [Route("ImportFromCatalog")]
    [Authorize(AssetRepositoryServiceConstants.DataModelManagementPolicy)]
    [ProducesResponseType(typeof(TransferModelResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ImportFromCatalog([FromBody] ImportFromCatalogRequestDto request)
    {
        try
        {
            var tenantId = HttpContext.GetTenantId();
            if (string.IsNullOrEmpty(tenantId))
            {
                return BadRequest(new OperationFailedErrorDto("TenantId is required"));
            }

            if (string.IsNullOrWhiteSpace(request.CatalogName))
            {
                return BadRequest(new OperationFailedErrorDto("CatalogName is required"));
            }

            if (string.IsNullOrWhiteSpace(request.ModelId))
            {
                return BadRequest(new OperationFailedErrorDto("ModelId is required"));
            }

            var ckModelId = new CkModelId(request.ModelId);
            var operationResult = new OperationResult();

            var compiledModel =
                await _catalogService.GetAsync(request.CatalogName, ckModelId, operationResult);

            if (compiledModel == null)
            {
                return NotFound();
            }

            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                return BadRequest(new OperationFailedErrorDto(
                    string.Join("; ", operationResult.Messages.Select(m => m.MessageText))));
            }

            // Check system dependency compatibility
            var tenantContext = await _systemContext.FindTenantContextAsync(tenantId);
            var sysVersions = await _libraryStatusService.GetInstalledSystemVersionsAsync(tenantContext);
            var (isCompatible, incompatibilityReason) = await _libraryStatusService.CheckSystemCompatibilityAsync(
                ckModelId, sysVersions, new HashSet<string>(), new List<string>(), CancellationToken.None);
            if (!isCompatible)
            {
                return BadRequest(new OperationFailedErrorDto(
                    $"Import blocked: {incompatibilityReason}"));
            }

            // AB#6870: tenant upgrade guard - before anything is cached or queued.
            var refusal = await EnforceUpgradeGuardAsync(tenantId, [ckModelId], request.ForceBreakingUpdate);
            if (refusal != null)
            {
                return refusal;
            }

            // Serialize the compiled model to JSON and cache it
            var cacheKey = await SerializeModelToCache(tenantId, compiledModel);

            var args = new ImportCkCommandRequest(tenantId, cacheKey);
            var r = await _importCkCommandClient.GetResponse<JobCreatedResponse>(args);
            return Ok(new TransferModelResponseDto(r.JobId));
        }
        catch (InvalidOperationException e)
        {
            return BadRequest(new InternalServerErrorDto(e.Message));
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    // POST: {tenantId}/v1/Models/ResolveDependencies
    /// <summary>
    ///     Resolves the full dependency tree for a CK model from a catalog and compares
    ///     it against the tenant's installed models to determine required actions.
    /// </summary>
    /// <param name="request">The catalog name and model ID to resolve</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Dependency tree with install/update/none actions per model</returns>
    [HttpPost]
    [Route("ResolveDependencies")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(DependencyResolutionResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ResolveDependencies(
        [FromBody] ImportFromCatalogRequestDto request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var tenantId = HttpContext.GetTenantId();
            if (string.IsNullOrEmpty(tenantId))
            {
                return BadRequest(new OperationFailedErrorDto("TenantId is required"));
            }

            if (string.IsNullOrWhiteSpace(request.CatalogName))
            {
                return BadRequest(new OperationFailedErrorDto("CatalogName is required"));
            }

            if (string.IsNullOrWhiteSpace(request.ModelId))
            {
                return BadRequest(new OperationFailedErrorDto("ModelId is required"));
            }

            var ckModelId = new CkModelId(request.ModelId);
            var operationResult = new OperationResult();

            var compiledModel =
                await _catalogService.GetAsync(request.CatalogName, ckModelId, operationResult,
                    cancellationToken: cancellationToken);

            if (compiledModel == null)
            {
                return NotFound();
            }

            // Get tenant context and installed system versions
            var tenantContext = await _systemContext.FindTenantContextAsync(tenantId);
            var installedVersions = await _libraryStatusService.GetInstalledModelVersionsAsync(tenantContext);

            // Resolve the dependency tree
            var rootItem = await CkImportDependencyResolver.ResolveRootAsync(compiledModel,
                tenantContext.IsCkModelExistingAsync, installedVersions, LoadCatalogModelAsync, cancellationToken);

            // AB#6870: the preview agrees with the refusal of the import - the flattened import set of the tree is
            // judged by the same tenant upgrade guard (read-only).
            var importSet = new List<string>();
            CkImportDependencyResolver.CollectModelsToImport(rootItem, importSet, new HashSet<string>());
            var blockers = await _libraryStatusService.EvaluateUpdateSetBlockersAsync(tenantId,
                importSet.Select(id => new CkModelId(id)).ToList(), cancellationToken);

            return Ok(new DependencyResolutionResponseDto
            {
                RootModel = rootItem,
                IsBlocked = blockers.Count > 0,
                Blockers = blockers.ToList()
            });
        }
        catch (InvalidOperationException e)
        {
            return BadRequest(new InternalServerErrorDto(e.Message));
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    // POST: {tenantId}/v1/Models/CheckUpgrade
    /// <summary>
    ///     Pre-flight check to determine if importing a CK model will trigger migrations and, read-only (AB#6868),
    ///     which installed models it would break (<c>isBlocked</c> / <c>blockers</c>)
    /// </summary>
    /// <param name="request">The catalog name and model ID to check</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Upgrade check information including migration availability and breaking changes</returns>
    [HttpPost]
    [Route("CheckUpgrade")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(UpgradeCheckResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> CheckUpgrade(
        [FromBody] ImportFromCatalogRequestDto request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var tenantId = HttpContext.GetTenantId();
            if (string.IsNullOrEmpty(tenantId))
            {
                return BadRequest(new OperationFailedErrorDto("TenantId is required"));
            }

            if (string.IsNullOrWhiteSpace(request.CatalogName))
            {
                return BadRequest(new OperationFailedErrorDto("CatalogName is required"));
            }

            if (string.IsNullOrWhiteSpace(request.ModelId))
            {
                return BadRequest(new OperationFailedErrorDto("ModelId is required"));
            }

            var ckModelId = new CkModelId(request.ModelId);

            // Verify model exists in catalog
            var exists = await _catalogService.IsExistingAsync(ckModelId);
            if (!exists)
            {
                return NotFound();
            }

            var upgradeInfo = await _upgradeService.CheckUpgradeNeededAsync(
                tenantId, ckModelId.Name, ckModelId.Version.ToString(), cancellationToken);

            // AB#6868: read-only tenant upgrade guard - which installed models would the target version break?
            // AB#6966: as an explicit caller - the file import judges service-managed models (System) too, so the
            // preview does as well. "HasBreakingChanges" below is the migration view; IsBlocked/Blockers the tenant guard.
            var blockers = await _libraryStatusService.EvaluateExplicitUpgradeBlockersAsync(tenantId, ckModelId,
                cancellationToken);

            return Ok(new UpgradeCheckResponseDto
            {
                ModelName = upgradeInfo.CkModelName,
                InstalledVersion = upgradeInfo.InstalledVersion,
                TargetVersion = upgradeInfo.TargetVersion,
                UpgradeNeeded = upgradeInfo.UpgradeNeeded,
                MigrationPathAvailable = upgradeInfo.MigrationPathAvailable,
                HasBreakingChanges = upgradeInfo.HasBreakingChanges,
                ErrorMessage = upgradeInfo.ErrorMessage,
                IsBlocked = blockers.Count > 0,
                Blockers = blockers.ToList()
            });
        }
        catch (InvalidOperationException e)
        {
            return BadRequest(new InternalServerErrorDto(e.Message));
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    // GET: {tenantId}/v1/Models/LibraryStatus
    /// <summary>
    ///     Returns the merged status of all CK model libraries: installed models
    ///     combined with catalog availability, version comparison, and action flags.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Combined library status for all known models</returns>
    [HttpGet]
    [Route("LibraryStatus")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(CkModelLibraryStatusResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetLibraryStatus(CancellationToken cancellationToken = default)
    {
        try
        {
            var tenantId = HttpContext.GetTenantId();
            if (string.IsNullOrEmpty(tenantId))
            {
                return BadRequest(new OperationFailedErrorDto("TenantId is required"));
            }

            // AB#5432: the computation lives in ICkModelLibraryStatusService so the periodic
            // CK-model observability sweep can ask the same question for a tenant it has no
            // request for. This endpoint is now only the HTTP shell around it.
            return Ok(await _libraryStatusService.GetLibraryStatusAsync(tenantId, cancellationToken));
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    // POST: {tenantId}/v1/Models/ResolveDependenciesBatch
    /// <summary>
    ///     Resolves dependencies for multiple CK models in a single call.
    ///     Returns a flattened, deduplicated, topologically sorted import list.
    /// </summary>
    /// <param name="requests">List of catalog name + model ID pairs to resolve</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Combined dependency resolution with flattened import list</returns>
    [HttpPost]
    [Route("ResolveDependenciesBatch")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(BatchDependencyResolutionResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ResolveDependenciesBatch(
        [FromBody] List<ImportFromCatalogRequestDto> requests,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var tenantId = HttpContext.GetTenantId();
            if (string.IsNullOrEmpty(tenantId))
            {
                return BadRequest(new OperationFailedErrorDto("TenantId is required"));
            }

            var tenantContext = await _systemContext.FindTenantContextAsync(tenantId);
            var installedVersions = await _libraryStatusService.GetInstalledModelVersionsAsync(tenantContext);
            var dependencyTrees = new List<DependencyResolutionResponseDto>();
            var allModelsToImport = new List<string>();
            var seen = new HashSet<string>();

            foreach (var request in requests)
            {
                if (string.IsNullOrWhiteSpace(request.CatalogName) ||
                    string.IsNullOrWhiteSpace(request.ModelId))
                {
                    continue;
                }

                var ckModelId = new CkModelId(request.ModelId);
                var operationResult = new OperationResult();
                var compiledModel = await _catalogService.GetAsync(
                    request.CatalogName, ckModelId, operationResult,
                    cancellationToken: cancellationToken);

                if (compiledModel == null) continue;

                var rootItem = await CkImportDependencyResolver.ResolveRootAsync(compiledModel,
                    tenantContext.IsCkModelExistingAsync, installedVersions, LoadCatalogModelAsync, cancellationToken);

                dependencyTrees.Add(new DependencyResolutionResponseDto { RootModel = rootItem });

                // Flatten this tree into the combined list
                CkImportDependencyResolver.CollectModelsToImport(rootItem, allModelsToImport, seen);
            }

            // Build lookup of final import versions by name
            var importVersionByName = new Dictionary<string, CkModelId>();
            foreach (var modelId in allModelsToImport)
            {
                var id = new CkModelId(modelId);
                importVersionByName[id.Name] = id;
            }

            // Correct tree items: if a higher version is in the import list,
            // mark lower-version dependencies as "none" to avoid confusion
            foreach (var tree in dependencyTrees)
            {
                CkImportDependencyResolver.CorrectTreeActions(tree.RootModel, importVersionByName);
            }

            // AB#6870: the whole import set is judged together by the tenant upgrade guard (read-only), so the preview
            // agrees with the refusal of ImportFromCatalogBatch.
            var blockers = await _libraryStatusService.EvaluateUpdateSetBlockersAsync(tenantId,
                allModelsToImport.Select(id => new CkModelId(id)).ToList(), cancellationToken);

            return Ok(new BatchDependencyResolutionResponseDto
            {
                ModelsToImport = allModelsToImport,
                DependencyTrees = dependencyTrees,
                IsBlocked = blockers.Count > 0,
                Blockers = blockers.ToList()
            });
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    // POST: {tenantId}/v1/Models/ImportFromCatalogBatch
    /// <summary>
    ///     Imports multiple CK models from a catalog in dependency order.
    ///     All models are cached upfront, then submitted as a single sequential batch job
    ///     to prevent race conditions during parallel dependency resolution.
    ///     <para>
    ///         Tenant upgrade guard (CK v2 F2.5, AB#6870): the whole set is judged together before anything is cached or
    ///         queued. A set that would break an installed model is refused <b>as a whole</b> with
    ///         <c>409 CK_UPGRADE_BLOCKED</c> (no partial import); <c>ForceBreakingUpdate = true</c> imports anyway and
    ///         logs the blockers and the caller at WARN.
    ///     </para>
    /// </summary>
    /// <param name="request">Catalog name and ordered list of model IDs</param>
    /// <returns>Single job ID for tracking the entire batch import</returns>
    [HttpPost]
    [Route("ImportFromCatalogBatch")]
    [Authorize(AssetRepositoryServiceConstants.DataModelManagementPolicy)]
    [ProducesResponseType(typeof(BatchImportResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ImportFromCatalogBatch(
        [FromBody] ImportFromCatalogBatchRequestDto request)
    {
        try
        {
            var tenantId = HttpContext.GetTenantId();
            if (string.IsNullOrEmpty(tenantId))
            {
                return BadRequest(new OperationFailedErrorDto("TenantId is required"));
            }

            if (string.IsNullOrWhiteSpace(request.CatalogName) || request.ModelIds.Count == 0)
            {
                return BadRequest(new OperationFailedErrorDto("CatalogName and at least one ModelId are required"));
            }

            // Pre-check system compatibility for all models
            var tenantContext = await _systemContext.FindTenantContextAsync(tenantId);
            var sysVersions = await _libraryStatusService.GetInstalledSystemVersionsAsync(tenantContext);

            foreach (var modelId in request.ModelIds)
            {
                var checkId = new CkModelId(modelId);
                var (isCompatible, reason) = await _libraryStatusService.CheckSystemCompatibilityAsync(
                    checkId, sysVersions, new HashSet<string>(), new List<string>(), CancellationToken.None);
                if (!isCompatible)
                {
                    return BadRequest(new OperationFailedErrorDto(
                        $"Import blocked for '{modelId}': {reason}"));
                }
            }

            // Load every model first, judge the whole set with the tenant upgrade guard (AB#6870), and only then
            // cache and queue: a refused set leaves nothing behind (no cache entry, no job, no partial import).
            var compiledModels = new List<CkCompiledModelRoot>();
            foreach (var modelId in request.ModelIds)
            {
                var ckModelId = new CkModelId(modelId);
                var operationResult = new OperationResult();
                var compiledModel = await _catalogService.GetAsync(
                    request.CatalogName, ckModelId, operationResult);

                if (compiledModel == null)
                {
                    return BadRequest(new OperationFailedErrorDto(
                        $"Model '{modelId}' not found in catalog '{request.CatalogName}'"));
                }

                compiledModels.Add(compiledModel);
            }

            var refusal = await EnforceUpgradeGuardAsync(tenantId,
                compiledModels.Select(m => m.ModelId).ToList(), request.ForceBreakingUpdate);
            if (refusal != null)
            {
                return refusal;
            }

            // Cache all models upfront, then submit a single batch job that imports
            // them sequentially. This prevents race conditions where parallel Hangfire
            // jobs try to resolve dependencies against models still in "Importing" state.
            var cacheKeys = new List<string>();
            foreach (var compiledModel in compiledModels)
            {
                var cacheKey = await SerializeModelToCache(tenantId, compiledModel);
                cacheKeys.Add(cacheKey);
            }

            if (cacheKeys.Count == 0)
            {
                return BadRequest(new OperationFailedErrorDto("No models were imported"));
            }

            // Submit as a single sequential batch job
            var batchArgs = new ImportCkBatchCommandRequest(tenantId, cacheKeys);
            var r = await _importCkBatchCommandClient.GetResponse<JobCreatedResponse>(batchArgs);

            return Ok(new BatchImportResponseDto { JobId = r.JobId });
        }
        catch (InvalidOperationException e)
        {
            return BadRequest(new InternalServerErrorDto(e.Message));
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    // GET: {tenantId}/v1/Models/{modelName}/MigrationHistory
    /// <summary>
    ///     Gets the migration history for a specific CK model in the tenant
    /// </summary>
    /// <param name="modelName">Name of the CK model (e.g., "Energy")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Migration history entries sorted by execution date descending</returns>
    [HttpGet]
    [Route("{modelName}/MigrationHistory")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(MigrationHistoryResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetMigrationHistory(
        [FromRoute] string modelName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var tenantId = HttpContext.GetTenantId();
            if (string.IsNullOrEmpty(tenantId))
            {
                return BadRequest(new OperationFailedErrorDto("TenantId is required"));
            }

            if (string.IsNullOrWhiteSpace(modelName))
            {
                return BadRequest(new OperationFailedErrorDto("ModelName is required"));
            }

            var history = await _migrationService.GetHistoryAsync(tenantId, modelName, cancellationToken);

            var response = new MigrationHistoryResponseDto
            {
                TotalCount = history.Count,
                Items = history.Select(h => new MigrationHistoryEntryDto
                {
                    CkModelName = h.CkModelName,
                    FromVersion = h.FromVersion,
                    ToVersion = h.ToVersion,
                    ExecutedAt = h.ExecutedAt,
                    Success = h.Success,
                    EntitiesAffected = h.EntitiesAffected,
                    EntitiesAdded = h.EntitiesAdded,
                    EntitiesUpdated = h.EntitiesUpdated,
                    EntitiesDeleted = h.EntitiesDeleted,
                    DurationMs = h.DurationMs,
                    Errors = h.Errors,
                    Warnings = h.Warnings,
                    BackupId = h.BackupId
                }).ToList()
            };

            return Ok(response);
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    /// <summary>
    ///     Tenant upgrade guard enforcement (AB#6870): judges <paramref name="updateSet" /> as one set against the
    ///     tenant. Returns <c>null</c> when the import may proceed (nothing breaks, or the caller forced it - then the
    ///     blockers and the caller are logged at WARN), otherwise the <c>409 CK_UPGRADE_BLOCKED</c> result. A guard
    ///     failure propagates (the import fails closed, it is not silently allowed).
    /// </summary>
    private async Task<IActionResult?> EnforceUpgradeGuardAsync(string tenantId, IReadOnlyList<CkModelId> updateSet,
        bool forceBreakingUpdate)
    {
        var blockers = await _libraryStatusService.EvaluateUpdateSetBlockersAsync(tenantId, updateSet,
            HttpContext.RequestAborted);
        if (blockers.Count == 0)
        {
            return null;
        }

        var requested = updateSet.Select(m => m.FullName).ToList();
        if (forceBreakingUpdate)
        {
            _logger.LogWarning(
                "Forced breaking CK update on tenant {TenantId} by {Caller}: importing {UpdateSet} breaks {BlockerCount} installed model(s): {Blockers}",
                tenantId, GetCallerDescription(), string.Join(", ", requested), blockers.Count,
                string.Join("; ", blockers.Select(b => $"{b.Dependent} [{b.Kind} {b.Subject}] by {b.Update}")));
            return null;
        }

        return CkUpgradeBlockedError.CreateResult(requested, blockers.ToList());
    }

    private string GetCallerDescription()
    {
        var user = HttpContext.User;
        var subject = user.FindFirst(JwtClaimTypes.Subject)?.Value;
        var clientId = user.FindFirst(JwtClaimTypes.ClientId)?.Value;
        var name = user.FindFirst(JwtClaimTypes.Name)?.Value ?? user.Identity?.Name;

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(subject)) parts.Add($"sub={subject}");
        if (!string.IsNullOrEmpty(clientId)) parts.Add($"client_id={clientId}");
        if (!string.IsNullOrEmpty(name)) parts.Add($"name={name}");
        return parts.Count > 0 ? string.Join(" ", parts) : "unknown caller";
    }

    private async Task<CkCompiledModelRoot?> LoadCatalogModelAsync(CkModelId modelId,
        CancellationToken cancellationToken) =>
        await _catalogService.GetAsync(modelId, new OperationResult(), cancellationToken: cancellationToken);

    private async Task<string> SerializeModelToCache(string tenantId,
        ConstructionKit.Contracts.DataTransferObjects.CkCompiledModelRoot compiledModel)
    {
        await using var memoryStream = new MemoryStream();
        await using var streamWriter = new StreamWriter(memoryStream, leaveOpen: true);
        await _ckJsonSerializer.SerializeAsync(streamWriter, compiledModel);
        await streamWriter.FlushAsync();
        memoryStream.Position = 0;

        var fileName = $"{compiledModel.ModelId.FullName}.json";
        var key = await _distributedCache.CreateStreamAsync(tenantId, memoryStream, "application/json", fileName,
            TimeSpan.FromHours(1));
        return key;
    }

    /// <summary>
    ///     AB#6966: reads the uploaded file the way the import job does (json or yaml; a zip is opened for its .json,
    ///     else its .yaml) so that the upgrade guard judges exactly what would be imported. Throws
    ///     <see cref="InvalidOperationException" /> (a 400) when the file cannot be read as a compiled CK model.
    /// </summary>
    private async Task<CkCompiledModelRoot> ReadCompiledModelAsync(IFormFile? file)
    {
        if (file == null || file.Length == 0)
        {
            throw new InvalidOperationException("A CK model file is required.");
        }

        await using var upload = file.OpenReadStream();
        var isZip = file.ContentType.Equals("application/zip", StringComparison.OrdinalIgnoreCase) ||
                    file.ContentType.Equals("application/x-zip-compressed", StringComparison.OrdinalIgnoreCase);
        var operationResult = new OperationResult();
        CkCompiledModelRoot? model;
        try
        {
            if (isZip)
            {
                using var archive = new ZipArchive(upload);
                var entry = archive.Entries.FirstOrDefault(e => Path.GetExtension(e.Name).Equals(".json", StringComparison.OrdinalIgnoreCase))
                            ?? archive.Entries.FirstOrDefault(e => Path.GetExtension(e.Name).Equals(".yaml", StringComparison.OrdinalIgnoreCase));
                if (entry == null)
                {
                    throw new InvalidOperationException("The zip archive contains no .json or .yaml CK model file.");
                }

                await using var entryStream = entry.Open();
                model = await _ckSerializer.DeserializeCompiledModelRootAsync(entryStream, entry.Name, operationResult);
            }
            else
            {
                model = await _ckSerializer.DeserializeCompiledModelRootAsync(upload, file.FileName, operationResult);
            }
        }
        catch (InvalidDataException e)
        {
            throw new InvalidOperationException($"The file '{file.FileName}' is not a readable CK model archive: {e.Message}");
        }

        if (model == null || operationResult.HasErrors || operationResult.HasFatalErrors)
        {
            var reasons = string.Join("; ", operationResult.Messages.Select(m => m.MessageText));
            throw new InvalidOperationException(
                $"The file '{file.FileName}' cannot be read as a compiled CK model{(reasons.Length > 0 ? ": " + reasons : ".")}");
        }

        return model;
    }

    /// <summary>
    ///     AB#6966: overriding the tenant upgrade guard is a data model operation, like importing from a catalog - the
    ///     <c>ImportCk</c> endpoint itself only needs the tenant read-write policy.
    /// </summary>
    private async Task<bool> MayForceBreakingUpdateAsync()
    {
        var authorization = await _authorizationService.AuthorizeAsync(HttpContext.User, null,
            AssetRepositoryServiceConstants.DataModelManagementPolicy);
        return authorization.Succeeded;
    }

    private async Task<string> AddFileToCache(string tenantId, IFormFile file)
    {
        await using var memoryStream = new MemoryStream();
        await file.CopyToAsync(memoryStream);
        memoryStream.Position = 0;
        var key = await _distributedCache.CreateStreamAsync(tenantId, memoryStream, file.ContentType, file.FileName,
            TimeSpan.FromHours(1));
        return key;
    }
}
