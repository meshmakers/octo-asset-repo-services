using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services;

/// <summary>
///     Computes the merged CK model library status for a tenant: installed models combined with
///     catalog availability, version comparison and transitive system-compatibility checks.
///
///     Extracted from <c>ModelsController</c> (AB#5432) for one reason: the same answer is now
///     needed by two callers with nothing in common. The REST endpoint asks for it on behalf of a
///     signed-in user with a tenant route and an HttpContext; the periodic observability sweep asks
///     for it for every opted-in tenant with neither. Everything this computation needs is a tenant
///     id, the system context and the catalogs — no request, no identity, no HTTP. Keeping it in a
///     controller forced the sweep to either duplicate it or fake a request.
///
///     That is also the seam the eventual move to octo-platform-services runs along: this type and
///     the two status DTOs are the whole payload, and neither touches ASP.NET.
/// </summary>
public interface ICkModelLibraryStatusService
{
    /// <summary>
    ///     Returns the merged library status for one tenant. Per-model failures are captured on the
    ///     row (<see cref="CkModelLibraryStatusItemDto.IsCompatible" /> false plus a reason) rather
    ///     than thrown, so one misbehaving model never collapses the whole answer. A failure to
    ///     reach the tenant at all still throws — the caller decides what that means.
    /// </summary>
    Task<CkModelLibraryStatusResponseDto> GetLibraryStatusAsync(string tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Installed versions of the service-managed (System*) models of a tenant, restricted to
    ///     models in state <c>Available</c>. This is the baseline every compatibility check is
    ///     evaluated against.
    /// </summary>
    Task<Dictionary<string, CkVersion>> GetInstalledSystemVersionsAsync(ITenantContext tenantContext);

    /// <summary>
    ///     Installed versions of ALL models of a tenant in state <c>Available</c> (highest version per model name).
    ///     Used to judge a catalog dependency against what is installed (CK v2 F1.0, AB#5900).
    /// </summary>
    Task<Dictionary<string, CkVersion>> GetInstalledModelVersionsAsync(ITenantContext tenantContext);

    /// <summary>
    ///     Walks the catalog dependency graph of <paramref name="catalogModelId" /> and decides
    ///     whether it can be installed against the tenant's installed system models.
    ///     <paramref name="unresolvedDependencies" /> collects model ids that could not be resolved
    ///     in any registered catalog — a non-empty list is a catalog publishing inconsistency, not a
    ///     tenant problem.
    /// </summary>
    Task<(bool isCompatible, string? reason)> CheckSystemCompatibilityAsync(
        CkModelId catalogModelId,
        Dictionary<string, CkVersion> installedSystemVersions,
        HashSet<string> visited,
        List<string> unresolvedDependencies,
        CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class CkModelLibraryStatusService : ICkModelLibraryStatusService
{
    /// <summary>
    ///     Upper bound on the models read per tenant, both installed and from the catalogs. Carried
    ///     over verbatim from the controller implementation this was extracted from.
    /// </summary>
    private const int ModelPageSize = 500;

    private readonly ICatalogService _catalogService;
    private readonly ISystemContext _systemContext;

    /// <summary>
    ///     Constructor
    /// </summary>
    public CkModelLibraryStatusService(ICatalogService catalogService, ISystemContext systemContext)
    {
        _catalogService = catalogService;
        _systemContext = systemContext;
    }

    /// <summary>
    ///     True for models the platform owns and a user must not update by hand (<c>System</c> and
    ///     everything under <c>System.</c>).
    /// </summary>
    public static bool IsSystemManaged(string modelName) =>
        modelName == "System" || modelName.StartsWith("System.", StringComparison.Ordinal);

    /// <inheritdoc />
    public async Task<CkModelLibraryStatusResponseDto> GetLibraryStatusAsync(string tenantId,
        CancellationToken cancellationToken = default)
    {
        // Get installed models from tenant
        var tenantContext = await _systemContext.FindTenantContextAsync(tenantId);
        var repository = tenantContext.GetTenantRepository();
        var session = repository.GetSession();
        var queryOptions = RtEntityQueryOptions.Create();
        var installedResult = await repository.GetCkModelsAsync(session, null, queryOptions, take: ModelPageSize);

        // Get catalog models
        var catalogResult = await _catalogService.ListAsync(0, ModelPageSize, cancellationToken: cancellationToken);
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
            items.Add(await BuildInstalledItemAsync(inst, catalogByName, installedSystemVersions, cancellationToken));
        }

        // Add catalog-only models (not installed)
        foreach (var (name, cm) in catalogByName)
        {
            if (!processedNames.Contains(name))
            {
                items.Add(await BuildCatalogOnlyItemAsync(name, cm, installedSystemVersions, cancellationToken));
            }
        }

        return new CkModelLibraryStatusResponseDto
        {
            Items = items,
            ModelsNeedingActionCount = items.Count(i => i.NeedsAction),
            ModelsWithCatalogInconsistencyCount = items.Count(i => i.HasCatalogInconsistency)
        };
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, CkVersion>> GetInstalledSystemVersionsAsync(ITenantContext tenantContext)
    {
        var repository = tenantContext.GetTenantRepository();
        var session = repository.GetSession();
        var queryOptions = RtEntityQueryOptions.Create();
        var installedResult = await repository.GetCkModelsAsync(session, null, queryOptions, take: ModelPageSize);

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

    /// <inheritdoc />
    public async Task<Dictionary<string, CkVersion>> GetInstalledModelVersionsAsync(ITenantContext tenantContext)
    {
        var repository = tenantContext.GetTenantRepository();
        var session = repository.GetSession();
        var installedResult = await repository.GetCkModelsAsync(session, null, RtEntityQueryOptions.Create(),
            take: ModelPageSize);

        var result = new Dictionary<string, CkVersion>();
        foreach (var inst in installedResult.Items.Where(i =>
                     i.ModelState == ConstructionKit.Contracts.DataTransferObjects.ModelState.Available))
        {
            if (!result.TryGetValue(inst.ModelId, out var existing) || inst.Id.Version.CompareTo(existing) > 0)
            {
                result[inst.ModelId] = inst.Id.Version;
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<(bool isCompatible, string? reason)> CheckSystemCompatibilityAsync(
        CkModelId catalogModelId,
        Dictionary<string, CkVersion> installedSystemVersions,
        HashSet<string> visited,
        List<string> unresolvedDependencies,
        CancellationToken cancellationToken)
    {
        var operationResult = new OperationResult();
        ConstructionKit.Contracts.DataTransferObjects.CkCompiledModelRoot? compiled;
        try
        {
            compiled = await _catalogService.GetAsync(catalogModelId, operationResult,
                cancellationToken: cancellationToken);
        }
        catch (ModelCatalogException)
        {
            // The catalog graph is inconsistent: a model up the chain pinned a dependency
            // on a version that is not published in any registered catalog. Surface this
            // as an incompatibility instead of failing the whole library-status response.
            unresolvedDependencies.Add(catalogModelId.FullName);
            return (false,
                $"Catalog inconsistency: required dependency '{catalogModelId.FullName}' is not available in any registered catalog");
        }

        if (compiled?.Dependencies == null) return (true, null);

        foreach (var dep in compiled.Dependencies)
        {
            if (!visited.Add(dep.FullName)) continue;

            if (IsSystemManaged(dep.Name))
            {
                if (installedSystemVersions.TryGetValue(dep.Name, out var installedVersion))
                {
                    // Strict version check: compiled models contain exact CkTypeId
                    // references (e.g. System-2.0.7/Entity-1) so the installed
                    // system version must match exactly.
                    if (installedVersion.CompareTo(dep.Version) != 0)
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
                    dep, installedSystemVersions, visited, unresolvedDependencies, cancellationToken);
                if (!subCompat) return (false, subReason);
            }
        }

        return (true, null);
    }

    // Build one library-status row for an installed model. Catches per-item failures so a
    // single misbehaving model does not collapse the whole library-status response into a 500.
    private async Task<CkModelLibraryStatusItemDto> BuildInstalledItemAsync(
        Runtime.Contracts.MongoDb.Repositories.Entities.CkModel inst,
        Dictionary<string, CatalogResultItem> catalogByName,
        Dictionary<string, CkVersion> installedSystemVersions,
        CancellationToken cancellationToken)
    {
        catalogByName.TryGetValue(inst.ModelId, out var catalog);
        var isServiceManaged = IsSystemManaged(inst.ModelId);
        var modelState = inst.ModelState.ToString();
        var dependencies = inst.Dependencies?.Select(d => d.FullName).ToList() ?? [];

        try
        {
            var hasUpdate = !isServiceManaged && catalog != null &&
                            catalog.ModelId.Version.CompareTo(inst.Id.Version) > 0;
            var isResolveFailed = inst.ModelState ==
                                  ConstructionKit.Contracts.DataTransferObjects.ModelState.ResolveFailed;

            var isCompatible = true;
            string? incompatibilityReason = null;
            var unresolvedDeps = new List<string>();
            if (!isServiceManaged && catalog != null && (hasUpdate || isResolveFailed))
            {
                (isCompatible, incompatibilityReason) = await CheckSystemCompatibilityAsync(
                    catalog.ModelId, installedSystemVersions, new HashSet<string>(),
                    unresolvedDeps, cancellationToken);
            }

            var hasInconsistency = unresolvedDeps.Count > 0;
            var needsAction = (isResolveFailed || hasUpdate) && !isServiceManaged
                                                            && isCompatible && !hasInconsistency;

            return new CkModelLibraryStatusItemDto
            {
                Name = inst.ModelId,
                InstalledVersion = inst.Id.Version.ToString(),
                ModelState = modelState,
                Dependencies = dependencies,
                CatalogVersion = catalog?.ModelId.Version.ToString(),
                HasUpdate = hasUpdate,
                NeedsAction = needsAction,
                CatalogName = catalog?.CatalogName,
                FullModelId = catalog?.ModelId.FullName,
                IsServiceManaged = isServiceManaged,
                IsCompatible = isCompatible,
                IncompatibilityReason = incompatibilityReason,
                UnresolvedDependencies = unresolvedDeps,
                HasCatalogInconsistency = hasInconsistency
            };
        }
        catch (Exception ex)
        {
            return new CkModelLibraryStatusItemDto
            {
                Name = inst.ModelId,
                InstalledVersion = inst.Id.Version.ToString(),
                ModelState = modelState,
                Dependencies = dependencies,
                CatalogVersion = catalog?.ModelId.Version.ToString(),
                CatalogName = catalog?.CatalogName,
                FullModelId = catalog?.ModelId.FullName,
                IsServiceManaged = isServiceManaged,
                IsCompatible = false,
                IncompatibilityReason = $"Failed to evaluate library status: {ex.Message}",
                HasCatalogInconsistency = true
            };
        }
    }

    private async Task<CkModelLibraryStatusItemDto> BuildCatalogOnlyItemAsync(
        string name,
        CatalogResultItem cm,
        Dictionary<string, CkVersion> installedSystemVersions,
        CancellationToken cancellationToken)
    {
        var isServiceManaged = IsSystemManaged(name);

        try
        {
            var isCompatible = true;
            string? incompatibilityReason = null;
            var unresolvedDeps = new List<string>();
            if (!isServiceManaged)
            {
                (isCompatible, incompatibilityReason) = await CheckSystemCompatibilityAsync(
                    cm.ModelId, installedSystemVersions, new HashSet<string>(),
                    unresolvedDeps, cancellationToken);
            }

            return new CkModelLibraryStatusItemDto
            {
                Name = name,
                CatalogVersion = cm.ModelId.Version.ToString(),
                CatalogName = cm.CatalogName,
                FullModelId = cm.ModelId.FullName,
                IsServiceManaged = isServiceManaged,
                IsCompatible = isCompatible,
                IncompatibilityReason = incompatibilityReason,
                UnresolvedDependencies = unresolvedDeps,
                HasCatalogInconsistency = unresolvedDeps.Count > 0
            };
        }
        catch (Exception ex)
        {
            return new CkModelLibraryStatusItemDto
            {
                Name = name,
                CatalogVersion = cm.ModelId.Version.ToString(),
                CatalogName = cm.CatalogName,
                FullModelId = cm.ModelId.FullName,
                IsServiceManaged = isServiceManaged,
                IsCompatible = false,
                IncompatibilityReason = $"Failed to evaluate library status: {ex.Message}",
                HasCatalogInconsistency = true
            };
        }
    }
}
