using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using IdentityModel;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;
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
    private readonly ICommandClient<ImportRtCommandRequest> _importRtCommandClient;
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
    /// <param name="catalogService">CK model catalog service</param>
    /// <param name="ckJsonSerializer">CK model JSON serializer</param>
    /// <param name="systemContext">System context for tenant access</param>
    /// <param name="upgradeService">CK model upgrade service for pre-flight checks</param>
    public ModelsController(IDistributedCacheService distributedCache,
        ICommandClient<ExportRtByQueryCommandRequest> exportRtByQueryCommandClient,
        ICommandClient<ExportRtByDeepGraphCommandRequest> exportRtByDeepGraphCommandClient,
        ICommandClient<ImportRtCommandRequest> importRtCommandClient,
        ICommandClient<ImportCkCommandRequest> importCkCommandClient,
        ICatalogService catalogService,
        ICkJsonSerializer ckJsonSerializer,
        ISystemContext systemContext,
        ICkModelUpgradeService upgradeService)
    {
        _distributedCache = distributedCache;
        _exportRtByQueryCommandClient = exportRtByQueryCommandClient;
        _exportRtByDeepGraphCommandClient = exportRtByDeepGraphCommandClient;
        _importRtCommandClient = importRtCommandClient;
        _importCkCommandClient = importCkCommandClient;
        _catalogService = catalogService;
        _ckJsonSerializer = ckJsonSerializer;
        _systemContext = systemContext;
        _upgradeService = upgradeService;
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
                exportModelRequestByDeepGraphDto.OriginCkTypeId);
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
            var sysVersions = await GetInstalledSystemVersionsAsync(tenantContext);
            var (isCompatible, incompatibilityReason) = await CheckSystemCompatibilityAsync(
                ckModelId, sysVersions, new HashSet<string>(), CancellationToken.None);
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
            var sysVersions = await GetInstalledSystemVersionsAsync(tenantContext);

            // Resolve the dependency tree
            var resolved = new HashSet<string>();
            var rootItem = await ResolveDependencyTreeAsync(
                compiledModel.ModelId, compiledModel.Dependencies,
                tenantContext, sysVersions, resolved, cancellationToken);

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

            // Get installed models from tenant
            var tenantContext = await _systemContext.FindTenantContextAsync(tenantId);
            var repository = tenantContext.GetTenantRepository();
            var session = repository.GetSession();
            var queryOptions = Runtime.Contracts.Repositories.Query.RtEntityQueryOptions.Create();
            var installedResult = await repository.GetCkModelsAsync(session, null, queryOptions, take: 500);

            // Get catalog models
            var catalogResult = await _catalogService.ListAsync(0, 500, cancellationToken: cancellationToken);
            var catalogModels = catalogResult.ModelResultItems;

            // Build catalog lookup: name → latest version (using semantic comparison)
            var catalogByName = new Dictionary<string, CatalogResultItem>();
            foreach (var cm in catalogModels)
            {
                if (!catalogByName.TryGetValue(cm.ModelId.Name, out var existing) ||
                    cm.ModelId.Version.CompareTo(existing.ModelId.Version) > 0)
                {
                    catalogByName[cm.ModelId.Name] = cm;
                }
            }

            // Build installed system model versions map for compatibility checks
            var installedSystemVersions = new Dictionary<string, CkVersion>();
            foreach (var inst in installedResult.Items)
            {
                if (IsSystemManaged(inst.ModelId) &&
                    inst.ModelState == ConstructionKit.Contracts.DataTransferObjects.ModelState.Available)
                {
                    installedSystemVersions[inst.ModelId] = inst.Id.Version;
                }
            }

            // Build merged view
            var items = new List<CkModelLibraryStatusItemDto>();
            var processedNames = new HashSet<string>();

            foreach (var inst in installedResult.Items)
            {
                processedNames.Add(inst.ModelId);
                catalogByName.TryGetValue(inst.ModelId, out var catalog);

                var isServiceManaged = IsSystemManaged(inst.ModelId);
                var hasUpdate = !isServiceManaged && catalog != null &&
                                catalog.ModelId.Version.CompareTo(inst.Id.Version) > 0;

                var modelState = inst.ModelState.ToString();
                var isResolveFailed = inst.ModelState ==
                                     ConstructionKit.Contracts.DataTransferObjects.ModelState.ResolveFailed;

                // Check compatibility for non-system models with catalog updates
                var isCompatible = true;
                string? incompatibilityReason = null;
                if (!isServiceManaged && catalog != null && hasUpdate)
                {
                    (isCompatible, incompatibilityReason) = await CheckSystemCompatibilityAsync(
                        catalog.ModelId, installedSystemVersions, new HashSet<string>(), cancellationToken);
                }

                var needsAction = (isResolveFailed || hasUpdate) && !isServiceManaged && isCompatible;

                items.Add(new CkModelLibraryStatusItemDto
                {
                    Name = inst.ModelId,
                    InstalledVersion = inst.Id.Version.ToString(),
                    ModelState = modelState,
                    Dependencies = inst.Dependencies?.Select(d => d.FullName).ToList() ?? [],
                    CatalogVersion = catalog?.ModelId.Version.ToString(),
                    HasUpdate = hasUpdate,
                    NeedsAction = needsAction,
                    CatalogName = catalog?.CatalogName,
                    FullModelId = catalog?.ModelId.FullName,
                    IsServiceManaged = isServiceManaged,
                    IsCompatible = isCompatible,
                    IncompatibilityReason = incompatibilityReason
                });
            }

            // Add catalog-only models (not installed)
            foreach (var (name, cm) in catalogByName)
            {
                if (!processedNames.Contains(name))
                {
                    var isServiceManaged = IsSystemManaged(name);

                    // Check compatibility for catalog-only non-system models
                    var isCompatible = true;
                    string? incompatibilityReason = null;
                    if (!isServiceManaged)
                    {
                        (isCompatible, incompatibilityReason) = await CheckSystemCompatibilityAsync(
                            cm.ModelId, installedSystemVersions, new HashSet<string>(), cancellationToken);
                    }

                    items.Add(new CkModelLibraryStatusItemDto
                    {
                        Name = name,
                        CatalogVersion = cm.ModelId.Version.ToString(),
                        CatalogName = cm.CatalogName,
                        FullModelId = cm.ModelId.FullName,
                        IsServiceManaged = isServiceManaged,
                        IsCompatible = isCompatible,
                        IncompatibilityReason = incompatibilityReason
                    });
                }
            }

            return Ok(new CkModelLibraryStatusResponseDto
            {
                Items = items,
                ModelsNeedingActionCount = items.Count(i => i.NeedsAction)
            });
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
            var sysVersions = await GetInstalledSystemVersionsAsync(tenantContext);
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
                    compiledModel.ModelId, compiledModel.Dependencies,
                    tenantContext, sysVersions, resolved, cancellationToken);

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
    ///     Each model is fetched, cached, and submitted for import sequentially.
    /// </summary>
    /// <param name="request">Catalog name and ordered list of model IDs</param>
    /// <returns>Job ID of the last import operation for tracking</returns>
    [HttpPost]
    [Route("ImportFromCatalogBatch")]
    [Authorize(AssetRepositoryServiceConstants.DataModelManagementPolicy)]
    [ProducesResponseType(typeof(TransferModelResponseDto), StatusCodes.Status200OK)]
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
            var sysVersions = await GetInstalledSystemVersionsAsync(tenantContext);

            foreach (var modelId in request.ModelIds)
            {
                var checkId = new CkModelId(modelId);
                var (isCompatible, reason) = await CheckSystemCompatibilityAsync(
                    checkId, sysVersions, new HashSet<string>(), CancellationToken.None);
                if (!isCompatible)
                {
                    return BadRequest(new OperationFailedErrorDto(
                        $"Import blocked for '{modelId}': {reason}"));
                }
            }

            // Submit each model as a separate Hangfire job via the async pipeline.
            // The frontend must wait for each job to complete before submitting the next
            // to ensure correct dependency order.
            var jobIds = new List<string>();

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
                var args = new ImportCkCommandRequest(tenantId, cacheKey);
                var r = await _importCkCommandClient.GetResponse<JobCreatedResponse>(args);
                jobIds.Add(r.JobId);
            }

            if (jobIds.Count == 0)
            {
                return BadRequest(new OperationFailedErrorDto("No models were imported"));
            }

            return Ok(new BatchImportResponseDto { JobIds = jobIds });
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

    private static void CollectModelsToImport(DependencyResolutionItemDto item, List<string> result,
        HashSet<string> seen)
    {
        foreach (var dep in item.Dependencies)
        {
            CollectModelsToImport(dep, result, seen);
        }

        if ((item.Action != "install" && item.Action != "update") || IsSystemManaged(item.Name) ||
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

    private async Task<DependencyResolutionItemDto> ResolveDependencyTreeAsync(
        CkModelId modelId,
        List<CkModelId>? dependencies,
        ITenantContext tenantContext,
        Dictionary<string, CkVersion>? installedSystemVersions,
        HashSet<string> resolved,
        CancellationToken cancellationToken)
    {
        var item = new DependencyResolutionItemDto
        {
            ModelId = modelId.FullName,
            Name = modelId.Name,
            RequiredVersion = modelId.Version.ToString()
        };

        // Check if exact version is installed
        var isInstalled = await tenantContext.IsCkModelExistingAsync(modelId);
        if (isInstalled)
        {
            item.InstalledVersion = modelId.Version.ToString();
            item.Action = "none";
        }
        else if (IsSystemManaged(modelId.Name))
        {
            // Service-managed models: check major version compatibility
            if (installedSystemVersions != null &&
                installedSystemVersions.TryGetValue(modelId.Name, out var installedSysVersion))
            {
                if (modelId.Version.Major != installedSysVersion.Major)
                {
                    item.Action = "incompatible";
                    item.InstalledVersion =
                        $"(requires major v{modelId.Version.Major}, installed v{installedSysVersion})";
                }
                else
                {
                    item.Action = "none";
                    item.InstalledVersion = $"(service-managed: v{installedSysVersion})";
                }
            }
            else
            {
                item.Action = "none";
                item.InstalledVersion = "(service-managed)";
            }
        }
        else
        {
            item.Action = "install";
        }

        // Resolve sub-dependencies
        if (dependencies != null)
        {
            foreach (var dep in dependencies)
            {
                // Prevent circular dependencies
                if (!resolved.Add(dep.FullName))
                {
                    continue;
                }

                // Fetch sub-dependency from catalog to get its dependencies
                List<CkModelId>? subDeps = null;
                var operationResult = new OperationResult();
                var depModel = await _catalogService.GetAsync(dep, operationResult,
                    cancellationToken: cancellationToken);
                if (depModel != null)
                {
                    subDeps = depModel.Dependencies;
                }

                var depItem = await ResolveDependencyTreeAsync(
                    dep, subDeps, tenantContext, installedSystemVersions, resolved, cancellationToken);
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

    private async Task<Dictionary<string, CkVersion>> GetInstalledSystemVersionsAsync(
        ITenantContext tenantContext)
    {
        var repository = tenantContext.GetTenantRepository();
        var session = repository.GetSession();
        var queryOptions = Runtime.Contracts.Repositories.Query.RtEntityQueryOptions.Create();
        var installedResult = await repository.GetCkModelsAsync(session, null, queryOptions, take: 500);

        var result = new Dictionary<string, CkVersion>();
        foreach (var inst in installedResult.Items)
        {
            if (IsSystemManaged(inst.ModelId) &&
                inst.ModelState == ConstructionKit.Contracts.DataTransferObjects.ModelState.Available)
            {
                result[inst.ModelId] = inst.Id.Version;
            }
        }

        return result;
    }

    private static bool HasIncompatibleDependency(DependencyResolutionItemDto item)
    {
        if (item.Action == "incompatible") return true;
        return item.Dependencies.Any(HasIncompatibleDependency);
    }

    private static bool IsSystemManaged(string modelName) =>
        modelName == "System" || modelName.StartsWith("System.", StringComparison.Ordinal);

    private async Task<(bool isCompatible, string? reason)> CheckSystemCompatibilityAsync(
        CkModelId catalogModelId,
        Dictionary<string, CkVersion> installedSystemVersions,
        HashSet<string> visited,
        CancellationToken cancellationToken)
    {
        var operationResult = new OperationResult();
        var compiled = await _catalogService.GetAsync(catalogModelId, operationResult,
            cancellationToken: cancellationToken);
        if (compiled?.Dependencies == null) return (true, null);

        foreach (var dep in compiled.Dependencies)
        {
            if (!visited.Add(dep.FullName)) continue;

            if (IsSystemManaged(dep.Name))
            {
                if (installedSystemVersions.TryGetValue(dep.Name, out var installedVersion))
                {
                    if (dep.Version.Major != installedVersion.Major ||
                        installedVersion.CompareTo(dep.Version) < 0)
                    {
                        return (false,
                            $"Requires {dep.FullName}, but {dep.Name}-{installedVersion} is installed");
                    }
                }
                else
                {
                    return (false, $"Requires {dep.FullName}, but {dep.Name} is not installed");
                }
            }
            else
            {
                var (subCompat, subReason) = await CheckSystemCompatibilityAsync(
                    dep, installedSystemVersions, visited, cancellationToken);
                if (!subCompat) return (false, subReason);
            }
        }

        return (true, null);
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
