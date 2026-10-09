using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using Duende.IdentityModel;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
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
    private readonly ICkJsonSerializer _ckJsonSerializer;
    private readonly IDistributedCacheService _distributedCache;
    private readonly ICommandClient<ExportRtByQueryCommandRequest> _exportRtByQueryCommandClient;
    private readonly ICommandClient<ExportRtByDeepGraphCommandRequest> _exportRtByDeepGraphCommandClient;
    private readonly ICommandClient<ImportCkCommandRequest> _importCkCommandClient;
    private readonly ICommandClient<ImportCkBatchCommandRequest> _importCkBatchCommandClient;
    private readonly ICommandClient<ImportRtCommandRequest> _importRtCommandClient;
    private readonly ICkModelLibraryStatusService _libraryStatusService;
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
        ICkModelLibraryStatusService libraryStatusService)
    {
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
    }

    // POST: {tenantId}/v1/Models/ExportRtByQuery
    /// <summary>
    ///     Exports a runtime model by query
    /// </summary>
    /// <param name="exportModelRequestByQueryDto">The query options for the export</param>
    /// <returns></returns>
    [HttpPost]
    [Route("ExportRtByQuery")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(TransferModelResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status400BadRequest)]
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

            var args = new ExportRtByQueryCommandRequest(tenantId, exportModelRequestByQueryDto.QueryId);
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
    ///     Exports a runtime model by deep graph
    /// </summary>
    /// <param name="exportModelRequestByDeepGraphDto">The deep graph options for the export</param>
    /// <returns></returns>
    [HttpPost]
    [Route("ExportRtByDeepGraph")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(TransferModelResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status400BadRequest)]
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

            var args = new ExportRtByDeepGraphCommandRequest(tenantId,
                exportModelRequestByDeepGraphDto.OriginRtIds,
                exportModelRequestByDeepGraphDto.OriginCkTypeId)
            {
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

    // POST: {tenantId}/v1/Models/ImportRt
    /// <summary>
    ///     Imports a runtime model
    /// </summary>
    /// <param name="importStrategy">The import strategy to use for the import</param>
    /// <param name="file">The file with the RT model definition</param>
    /// <returns></returns>
    [HttpPost]
    [RequestSizeLimit(300_000_000)]
    [Route("ImportRt")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    [ProducesResponseType(typeof(TransferModelResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status400BadRequest)]
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
            var cacheKey = await AddFileToCache(tenantId, file);
            var args = new ImportRtCommandRequest(tenantId, insertStrategy, cacheKey);
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
    /// </summary>
    /// <param name="file">The file with the CK model definition</param>
    /// <returns></returns>
    [HttpPost]
    [Route("ImportCk")]
    [Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy)]
    [ProducesResponseType(typeof(TransferModelResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ImportCk(IFormFile file)
    {
        try
        {
            var tenantId = HttpContext.GetTenantId();
            if (string.IsNullOrEmpty(tenantId))
            {
                return BadRequest(new OperationFailedErrorDto("TenantId is required"));
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
    /// </summary>
    /// <param name="request">The catalog name and model ID to import</param>
    /// <returns>A job ID for tracking the async import operation</returns>
    [HttpPost]
    [Route("ImportFromCatalog")]
    [Authorize(AssetRepositoryServiceConstants.DataModelManagementPolicy)]
    [ProducesResponseType(typeof(TransferModelResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
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
            var resolved = new HashSet<string>();
            var rootItem = await ResolveDependencyTreeAsync(
                compiledModel.ModelId, DependencyRequirement.Exact(compiledModel.ModelId), compiledModel,
                tenantContext, installedVersions, resolved, cancellationToken);

            return Ok(new DependencyResolutionResponseDto { RootModel = rootItem });
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
    ///     Pre-flight check to determine if importing a CK model will trigger migrations
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

            return Ok(new UpgradeCheckResponseDto
            {
                ModelName = upgradeInfo.CkModelName,
                InstalledVersion = upgradeInfo.InstalledVersion,
                TargetVersion = upgradeInfo.TargetVersion,
                UpgradeNeeded = upgradeInfo.UpgradeNeeded,
                MigrationPathAvailable = upgradeInfo.MigrationPathAvailable,
                HasBreakingChanges = upgradeInfo.HasBreakingChanges,
                ErrorMessage = upgradeInfo.ErrorMessage
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

                var resolved = new HashSet<string>();
                var rootItem = await ResolveDependencyTreeAsync(
                    compiledModel.ModelId, DependencyRequirement.Exact(compiledModel.ModelId), compiledModel,
                    tenantContext, installedVersions, resolved, cancellationToken);

                dependencyTrees.Add(new DependencyResolutionResponseDto { RootModel = rootItem });

                // Flatten this tree into the combined list
                CollectModelsToImport(rootItem, allModelsToImport, seen);
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
                CorrectTreeActions(tree.RootModel, importVersionByName);
            }

            return Ok(new BatchDependencyResolutionResponseDto
            {
                ModelsToImport = allModelsToImport,
                DependencyTrees = dependencyTrees
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
    /// </summary>
    /// <param name="request">Catalog name and ordered list of model IDs</param>
    /// <returns>Single job ID for tracking the entire batch import</returns>
    [HttpPost]
    [Route("ImportFromCatalogBatch")]
    [Authorize(AssetRepositoryServiceConstants.DataModelManagementPolicy)]
    [ProducesResponseType(typeof(BatchImportResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
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

            // Cache all models upfront, then submit a single batch job that imports
            // them sequentially. This prevents race conditions where parallel Hangfire
            // jobs try to resolve dependencies against models still in "Importing" state.
            var cacheKeys = new List<string>();

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

    private static void CollectModelsToImport(DependencyResolutionItemDto item, List<string> result,
        HashSet<string> seen)
    {
        foreach (var dep in item.Dependencies)
        {
            CollectModelsToImport(dep, result, seen);
        }

        if ((item.Action != "install" && item.Action != "update") || CkModelLibraryStatusService.IsSystemManaged(item.Name) ||
            HasIncompatibleDependency(item))
        {
            return;
        }

        // Deduplicate by model name - keep the highest version
        if (!seen.Add(item.Name))
        {
            // Already have this model name - replace if new version is higher
            var existingIndex = result.FindIndex(r => r.StartsWith(item.Name + "-", StringComparison.Ordinal));
            if (existingIndex >= 0)
            {
                var existingId = new CkModelId(result[existingIndex]);
                var newId = new CkModelId(item.ModelId);
                if (newId.Version.CompareTo(existingId.Version) > 0)
                {
                    result[existingIndex] = item.ModelId;
                }
            }
        }
        else
        {
            result.Add(item.ModelId);
        }
    }

    /// <summary>
    ///     What the parent model requires of a dependency: the exact compile-time pin of a classic model, or the declared
    ///     range + floor of a range-retaining model (CK v2 F1.0 / G3 review A-M1).
    /// </summary>
    private sealed record DependencyRequirement(CkModelId Pin, CkModelDependencyDto? Range)
    {
        public static DependencyRequirement Exact(CkModelId pin) => new(pin, null);

        public static DependencyRequirement For(CkModelId pin, CkCompiledModelRoot? parent) =>
            new(pin, parent?.DependencyRanges?.FirstOrDefault(r => r.Range.Name == pin.Name));

        /// <summary>
        ///     True when the installed version satisfies the requirement: exact pin -> the same version; range -> within
        ///     the effective range (>= floor). Never across majors.
        /// </summary>
        public bool IsSatisfiedBy(CkVersion installed)
        {
            if (installed.Major != Pin.Version.Major)
            {
                return false;
            }

            return Range == null
                ? installed.CompareTo(Pin.Version) == 0
                : Range.IsSatisfiedBy(new CkModelId(Pin.Name, installed.ToString()));
        }

        public string Describe() => Range == null ? $"v{Pin.Version}" : Range.ToString();
    }

    private async Task<DependencyResolutionItemDto> ResolveDependencyTreeAsync(
        CkModelId modelId,
        DependencyRequirement requirement,
        CkCompiledModelRoot? compiledModel,
        ITenantContext tenantContext,
        Dictionary<string, CkVersion> installedVersions,
        HashSet<string> resolved,
        CancellationToken cancellationToken)
    {
        var item = new DependencyResolutionItemDto
        {
            ModelId = modelId.FullName,
            Name = modelId.Name,
            RequiredVersion = requirement.Range?.Range.ToString() ?? modelId.Version.ToString()
        };

        var isServiceManaged = CkModelLibraryStatusService.IsSystemManaged(modelId.Name);
        var isInstalled = await tenantContext.IsCkModelExistingAsync(modelId);
        if (isInstalled)
        {
            item.InstalledVersion = modelId.Version.ToString();
            item.Action = "none";
        }
        else if (installedVersions.TryGetValue(modelId.Name, out var installedVersion))
        {
            // Judged by what the PARENT requires (G3 review A-M1): a classic parent is pinned to the exact version
            // (a newer installed version makes it ResolveFailed), a range-retaining parent accepts its range from
            // the floor; never across majors. A newer installed version is never offered for a downgrade install.
            if (requirement.IsSatisfiedBy(installedVersion))
            {
                item.Action = "none";
                item.InstalledVersion = isServiceManaged
                    ? $"(service-managed: v{installedVersion})"
                    : installedVersion.ToString();
            }
            else if (isServiceManaged || installedVersion.CompareTo(modelId.Version) > 0)
            {
                item.Action = "incompatible";
                item.InstalledVersion = $"(requires {requirement.Describe()}, installed v{installedVersion})";
            }
            else
            {
                // Older installed version: the import upgrades it.
                item.Action = "install";
                item.InstalledVersion = installedVersion.ToString();
            }
        }
        else if (isServiceManaged)
        {
            item.Action = "none";
            item.InstalledVersion = "(service-managed)";
        }
        else
        {
            item.Action = "install";
        }

        // Resolve sub-dependencies
        if (compiledModel?.Dependencies != null)
        {
            foreach (var dep in compiledModel.Dependencies)
            {
                // Prevent circular dependencies
                if (!resolved.Add(dep.FullName))
                {
                    continue;
                }

                // Fetch sub-dependency from catalog to get its dependencies
                var operationResult = new OperationResult();
                var depModel = await _catalogService.GetAsync(dep, operationResult,
                    cancellationToken: cancellationToken);

                var depItem = await ResolveDependencyTreeAsync(dep, DependencyRequirement.For(dep, compiledModel),
                    depModel, tenantContext, installedVersions, resolved, cancellationToken);
                item.Dependencies.Add(depItem);
            }
        }

        return item;
    }

    private static void CorrectTreeActions(DependencyResolutionItemDto item,
        Dictionary<string, CkModelId> importVersionByName)
    {
        foreach (var dep in item.Dependencies)
        {
            CorrectTreeActions(dep, importVersionByName);
        }

        // If this item shows "install" but a higher version of the same model
        // is already in the import list, mark as "none" (covered by higher version)
        if (item.Action is "install" or "update" &&
            importVersionByName.TryGetValue(item.Name, out var importVersion))
        {
            var itemVersion = new CkVersion(item.RequiredVersion);
            if (importVersion.Version.CompareTo(itemVersion) > 0)
            {
                item.Action = "none";
                item.InstalledVersion = $"(will import {importVersion.FullName})";
            }
        }
    }

    private static bool HasIncompatibleDependency(DependencyResolutionItemDto item)
    {
        if (item.Action == "incompatible") return true;
        return item.Dependencies.Any(HasIncompatibleDependency);
    }

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
