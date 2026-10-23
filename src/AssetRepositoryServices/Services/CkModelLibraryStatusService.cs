using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.UpgradeGuard;
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
    ///     Tenant upgrade guard for one candidate (CheckUpgrade, AB#6868): which installed models would importing
    ///     <paramref name="candidate" /> break? Read-only - delegates to <c>ITenantContext.EvaluateCkUpdateAsync</c>
    ///     (AB#6867), which writes nothing and starts no job. Empty when nothing would break, when the candidate is not
    ///     installed or is service-managed, or when it is not published in a catalog. A failure propagates.
    /// </summary>
    Task<IReadOnlyList<CkUpgradeBlockerDto>> EvaluateUpgradeBlockersAsync(string tenantId, CkModelId candidate,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Tenant upgrade guard for a whole update set (AB#6870): which installed models would importing all of
    ///     <paramref name="updateSet" /> together break? The set is judged as one - a dependent that is itself updated in
    ///     the set (Registration 2.10.0 next to Basic 2.5.0) is judged by its new version. Read-only; this is what the
    ///     import endpoints enforce and the resolve endpoints preview. Empty when nothing would break. A failure
    ///     propagates (the import fails closed).
    /// </summary>
    Task<IReadOnlyList<CkUpgradeBlockerDto>> EvaluateUpdateSetBlockersAsync(string tenantId,
        IReadOnlyList<CkModelId> updateSet, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Tenant upgrade guard for the models a blueprint installs or raises (AB#7029): judges <paramref name="roots" />
    ///     together with the dependency updates their import pulls in - the same set the import dialog
    ///     (<c>ResolveDependencies</c>) and a library status row (AB#7017) judge, from the same
    ///     <c>CkImportDependencyResolver</c>. A root that is not published in a catalog is judged alone. Read-only; a
    ///     failure propagates (the blueprint operation fails closed). Empty when nothing would break.
    /// </summary>
    Task<IReadOnlyList<CkUpgradeBlockerDto>> EvaluateImportSetBlockersAsync(string tenantId,
        IReadOnlyList<CkModelId> roots, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The read-only preview for an <b>explicit</b> caller (AB#6966, <c>CheckUpgrade</c>): like
    ///     <see cref="EvaluateUpgradeBlockersAsync" />, but a service-managed candidate (<c>System</c>, <c>System.*</c>) is
    ///     judged too, because the file import judges it as well (a preview must agree with the import).
    /// </summary>
    Task<IReadOnlyList<CkUpgradeBlockerDto>> EvaluateExplicitUpgradeBlockersAsync(string tenantId, CkModelId candidate,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     The guard of the file import (AB#6966, <c>ImportCk</c>): which installed models would importing the already
    ///     compiled <paramref name="candidates" /> (the uploaded file) break? Judged as an explicit caller, so
    ///     service-managed candidates (<c>System</c>, <c>System.*</c>) and downgrades are judged too. The catalogs are
    ///     only consulted for the suggestion of a newer dependent version. Read-only; a failure propagates (the import
    ///     fails closed).
    /// </summary>
    Task<IReadOnlyList<CkUpgradeBlockerDto>> EvaluateFileImportBlockersAsync(string tenantId,
        IReadOnlyList<ConstructionKit.Contracts.DataTransferObjects.CkCompiledModelRoot> candidates,
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

        // Highest Available version per installed model: the same map the import dialog resolves against (AB#7017).
        var snapshot = BuildInstalledSnapshot(installedResult.Items);

        // One memo for the whole evaluation: shared dependencies are fetched once, not once per row.
        var memo = new CatalogModelMemo();

        // Versions the catalogs offer: lets the upgrade guard suggest a newer version of a blocking dependent.
        var availableVersions = catalogModels.Select(m => m.ModelId).ToList();

        // Build merged view
        var items = new List<CkModelLibraryStatusItemDto>();
        var processedNames = new HashSet<string>();

        foreach (var inst in installedResult.Items)
        {
            processedNames.Add(inst.ModelId);
            items.Add(await BuildInstalledItemAsync(inst, catalogByName, installedSystemVersions,
                snapshot, tenantContext, availableVersions, memo, cancellationToken));
        }

        // Add catalog-only models (not installed)
        foreach (var (name, cm) in catalogByName)
        {
            if (!processedNames.Contains(name))
            {
                items.Add(await BuildCatalogOnlyItemAsync(name, cm, installedSystemVersions, memo, cancellationToken));
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
    public async Task<IReadOnlyList<CkUpgradeBlockerDto>> EvaluateUpgradeBlockersAsync(string tenantId,
        CkModelId candidate, CancellationToken cancellationToken = default)
    {
        var tenantContext = await _systemContext.FindTenantContextAsync(tenantId);
        var catalogResult = await _catalogService.ListAsync(0, ModelPageSize, cancellationToken: cancellationToken);
        var availableVersions = catalogResult.ModelResultItems.Select(m => m.ModelId).ToList();

        return await EvaluateBlockersAsync(tenantContext, candidate, availableVersions, new CatalogModelMemo(),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CkUpgradeBlockerDto>> EvaluateUpdateSetBlockersAsync(string tenantId,
        IReadOnlyList<CkModelId> updateSet, CancellationToken cancellationToken = default)
    {
        if (updateSet.Count == 0)
        {
            return [];
        }

        var tenantContext = await _systemContext.FindTenantContextAsync(tenantId);
        var catalogResult = await _catalogService.ListAsync(0, ModelPageSize, cancellationToken: cancellationToken);
        var availableVersions = catalogResult.ModelResultItems.Select(m => m.ModelId).ToList();

        return await EvaluateBlockersAsync(tenantContext, updateSet, availableVersions, new CatalogModelMemo(),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CkUpgradeBlockerDto>> EvaluateImportSetBlockersAsync(string tenantId,
        IReadOnlyList<CkModelId> roots, CancellationToken cancellationToken = default)
    {
        if (roots.Count == 0)
        {
            return [];
        }

        var tenantContext = await _systemContext.FindTenantContextAsync(tenantId);
        var installedResult = await tenantContext.GetTenantRepository().GetCkModelsAsync(
            tenantContext.GetTenantRepository().GetSession(), null, RtEntityQueryOptions.Create(),
            take: ModelPageSize);
        var catalogResult = await _catalogService.ListAsync(0, ModelPageSize, cancellationToken: cancellationToken);
        var availableVersions = catalogResult.ModelResultItems.Select(m => m.ModelId).ToList();
        var memo = new CatalogModelMemo();

        var updateSet = await ResolveImportSetAsync(roots, BuildInstalledSnapshot(installedResult.Items), memo,
            cancellationToken);
        return await EvaluateBlockersAsync(tenantContext, updateSet, availableVersions, memo, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CkUpgradeBlockerDto>> EvaluateExplicitUpgradeBlockersAsync(string tenantId,
        CkModelId candidate, CancellationToken cancellationToken = default)
    {
        var tenantContext = await _systemContext.FindTenantContextAsync(tenantId);
        var catalogResult = await _catalogService.ListAsync(0, ModelPageSize, cancellationToken: cancellationToken);
        var availableVersions = catalogResult.ModelResultItems.Select(m => m.ModelId).ToList();

        return await EvaluateBlockersAsync(tenantContext, [candidate], availableVersions, new CatalogModelMemo(),
            cancellationToken, explicitCaller: true);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CkUpgradeBlockerDto>> EvaluateFileImportBlockersAsync(string tenantId,
        IReadOnlyList<ConstructionKit.Contracts.DataTransferObjects.CkCompiledModelRoot> candidates,
        CancellationToken cancellationToken = default)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        var tenantContext = await _systemContext.FindTenantContextAsync(tenantId);
        var catalogResult = await _catalogService.ListAsync(0, ModelPageSize, cancellationToken: cancellationToken);
        var availableVersions = catalogResult.ModelResultItems.Select(m => m.ModelId).ToList();

        return await EvaluateCompiledBlockersAsync(tenantContext, candidates, availableVersions, new CatalogModelMemo(),
            true, cancellationToken);
    }

    private Task<List<CkUpgradeBlockerDto>> EvaluateBlockersAsync(ITenantContext tenantContext,
        CkModelId candidate, IReadOnlyCollection<CkModelId> availableVersions, CatalogModelMemo memo,
        CancellationToken cancellationToken) =>
        EvaluateBlockersAsync(tenantContext, [candidate], availableVersions, memo, cancellationToken);

    /// <summary>
    ///     The update set a library status row is judged with (AB#7017): the offered version of the row plus the
    ///     dependency updates its import pulls in - exactly the set the import dialog (<c>ResolveDependencies</c>) builds,
    ///     from the same <see cref="CkImportDependencyResolver" />, so row and dialog cannot disagree. Falls back to the
    ///     offered version alone when it is not published or the dialog would not import it (incompatible branch), which
    ///     is how the row was judged before.
    /// </summary>
    private async Task<IReadOnlyList<CkModelId>> ResolveUpdateSetAsync(CkModelId candidate,
        InstalledSnapshot installed, CatalogModelMemo memo, CancellationToken cancellationToken)
    {
        var importSet = await ImportSetOfAsync(candidate, installed, memo, cancellationToken);
        return importSet.Any(m => string.Equals(m.Name, candidate.Name, StringComparison.Ordinal))
            ? importSet
            : [candidate];
    }

    /// <summary>
    ///     The update set of a blueprint (AB#7029): every root (the blueprint's declared model at its floor version) plus
    ///     the dependency updates its import pulls in, one entry per model name at its highest version. A root is always
    ///     part of the result, also when it is not published or the resolver leaves its branch out (incompatible): it
    ///     is then judged alone, as before.
    /// </summary>
    private async Task<IReadOnlyList<CkModelId>> ResolveImportSetAsync(IReadOnlyList<CkModelId> roots,
        InstalledSnapshot installed, CatalogModelMemo memo, CancellationToken cancellationToken)
    {
        var byName = new Dictionary<string, CkModelId>(StringComparer.Ordinal);
        var order = new List<string>();

        void Add(CkModelId id)
        {
            if (!byName.TryGetValue(id.Name, out var existing))
            {
                byName[id.Name] = id;
                order.Add(id.Name);
            }
            else if (id.Version.CompareTo(existing.Version) > 0)
            {
                byName[id.Name] = id;
            }
        }

        foreach (var root in roots)
        {
            foreach (var pulledIn in await ImportSetOfAsync(root, installed, memo, cancellationToken))
            {
                Add(pulledIn);
            }

            Add(root);
        }

        return order.Select(n => byName[n]).ToList();
    }

    private async Task<IReadOnlyList<CkModelId>> ImportSetOfAsync(CkModelId candidate, InstalledSnapshot installed,
        CatalogModelMemo memo, CancellationToken cancellationToken)
    {
        var (found, root) = await memo.GetAsync(_catalogService, candidate, cancellationToken);
        if (!found || root == null)
        {
            return [candidate];
        }

        var tree = await CkImportDependencyResolver.ResolveRootAsync(root,
            id => Task.FromResult(installed.Ids.Contains(id.FullName)), installed.Versions,
            async (id, ct) =>
            {
                var (depFound, dep) = await memo.GetAsync(_catalogService, id, ct);
                return depFound ? dep : null;
            }, cancellationToken);

        var importSet = new List<string>();
        CkImportDependencyResolver.CollectModelsToImport(tree, importSet, new HashSet<string>());
        return importSet.Select(id => new CkModelId(id)).ToList();
    }

    private static InstalledSnapshot BuildInstalledSnapshot(IEnumerable<CkModel> installedModels)
    {
        var versions = new Dictionary<string, CkVersion>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var inst in installedModels.Where(i =>
                     i.ModelState == ConstructionKit.Contracts.DataTransferObjects.ModelState.Available))
        {
            ids.Add(inst.Id.FullName);
            if (!versions.TryGetValue(inst.ModelId, out var existing) || inst.Id.Version.CompareTo(existing) > 0)
            {
                versions[inst.ModelId] = inst.Id.Version;
            }
        }

        return new InstalledSnapshot(versions, ids);
    }

    /// <summary>
    ///     The installed <c>Available</c> models of the tenant, read once per status call (AB#7017): the highest version
    ///     per name and the exact ids - the same answers the import dialog gets from the tenant, without a lookup per
    ///     dependency.
    /// </summary>
    private sealed record InstalledSnapshot(IReadOnlyDictionary<string, CkVersion> Versions, IReadOnlySet<string> Ids);

    private static Task<CkUpgradeGuardResult> JudgeAsync(ITenantContext tenantContext,
        IReadOnlyCollection<ConstructionKit.Contracts.DataTransferObjects.CkCompiledModelRoot> set,
        IReadOnlyCollection<CkModelId> availableVersions, bool explicitCaller, CancellationToken cancellationToken) =>
        explicitCaller
            ? tenantContext.EvaluateExplicitCkImportAsync(set, availableVersions, cancellationToken)
            : tenantContext.EvaluateCkUpdateAsync(set, availableVersions, cancellationToken);

    /// <summary>
    ///     Runs the tenant upgrade guard for one candidate and maps the blockers. A suggested newer version of a blocking
    ///     dependent is only passed on as a <see cref="CkUpgradeBlockerDto.ResolveHint" /> when it is <b>proven</b>
    ///     against the combined update set (AB#6867 left this to the caller): the candidate plus the suggested compiled
    ///     dependent are evaluated together, and the hint stays only if no blocker for that dependent remains.
    ///     The candidates are judged as one set against the current installed state; a library status row passes its
    ///     candidate together with the dependency updates its import pulls in (AB#7017).
    /// </summary>
    private async Task<List<CkUpgradeBlockerDto>> EvaluateBlockersAsync(ITenantContext tenantContext,
        IReadOnlyList<CkModelId> candidates, IReadOnlyCollection<CkModelId> availableVersions, CatalogModelMemo memo,
        CancellationToken cancellationToken, bool explicitCaller = false)
    {
        // A candidate that is not published in a catalog cannot be judged and is left out (as for one candidate).
        var compiled = new List<ConstructionKit.Contracts.DataTransferObjects.CkCompiledModelRoot>();
        foreach (var candidate in candidates)
        {
            var (found, root) = await memo.GetAsync(_catalogService, candidate, cancellationToken);
            if (found && root != null)
            {
                compiled.Add(root);
            }
        }

        if (compiled.Count == 0)
        {
            return [];
        }

        return await EvaluateCompiledBlockersAsync(tenantContext, compiled, availableVersions, memo, explicitCaller,
            cancellationToken);
    }

    private async Task<List<CkUpgradeBlockerDto>> EvaluateCompiledBlockersAsync(ITenantContext tenantContext,
        IReadOnlyList<ConstructionKit.Contracts.DataTransferObjects.CkCompiledModelRoot> compiled,
        IReadOnlyCollection<CkModelId> availableVersions, CatalogModelMemo memo, bool explicitCaller,
        CancellationToken cancellationToken)
    {
        var result = await JudgeAsync(tenantContext, compiled, availableVersions, explicitCaller, cancellationToken);
        if (!result.IsBlocked)
        {
            return [];
        }

        var blockers = new List<CkUpgradeBlockerDto>();
        foreach (var blocker in result.Blockers)
        {
            var dto = ToDto(blocker);
            if (blocker.SuggestedDependentUpdate is { } suggestion &&
                await IsSuggestionProvenAsync(tenantContext, compiled, blocker, suggestion, availableVersions, memo,
                    explicitCaller, cancellationToken))
            {
                dto.SuggestedDependentUpdate = suggestion.FullName;
                dto.ResolveHint = $"update {suggestion.Name} to {suggestion.Version} first";
            }

            blockers.Add(dto);
        }

        return blockers;
    }

    private async Task<bool> IsSuggestionProvenAsync(ITenantContext tenantContext,
        IReadOnlyList<ConstructionKit.Contracts.DataTransferObjects.CkCompiledModelRoot> candidates,
        CkUpgradeBlocker blocker, CkModelId suggestion, IReadOnlyCollection<CkModelId> availableVersions,
        CatalogModelMemo memo, bool explicitCaller, CancellationToken cancellationToken)
    {
        var (found, suggested) = await memo.GetAsync(_catalogService, suggestion, cancellationToken);
        if (!found || suggested == null)
        {
            return false;
        }

        var combined = await JudgeAsync(tenantContext, [.. candidates, suggested], availableVersions, explicitCaller,
            cancellationToken);
        return !combined.Blockers.Any(b =>
            string.Equals(b.Dependent.Name, blocker.Dependent.Name, StringComparison.OrdinalIgnoreCase));
    }

    private static CkUpgradeBlockerDto ToDto(CkUpgradeBlocker blocker) => new()
    {
        Dependent = blocker.Dependent.FullName,
        Update = blocker.Update.FullName,
        Kind = blocker.Kind.ToString(),
        Subject = blocker.Subject,
        Code = blocker.Code,
        Message = $"{blocker.Dependent.Name} {blocker.Dependent.Version} would break: {blocker.Message}",
        DependentIsInUpdateSet = blocker.DependentIsInUpdateSet
    };

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
    public Task<(bool isCompatible, string? reason)> CheckSystemCompatibilityAsync(
        CkModelId catalogModelId,
        Dictionary<string, CkVersion> installedSystemVersions,
        HashSet<string> visited,
        List<string> unresolvedDependencies,
        CancellationToken cancellationToken)
    {
        return CheckSystemCompatibilityAsync(catalogModelId, installedSystemVersions, visited,
            unresolvedDependencies, new CatalogModelMemo(), cancellationToken);
    }

    /// <summary>
    ///     Per-evaluation memo of catalog lookups (AB#6330). One library-status call checks many rows whose
    ///     dependency graphs share models (Basic, ...); a published model version is immutable, so each model
    ///     is fetched at most once per call. The <c>visited</c> set stays per row on purpose — it prunes the
    ///     walk and therefore decides the result of a row. Only "found" and "not published" are memoized;
    ///     any other exception propagates to the row-level handler and is not remembered.
    /// </summary>
    private sealed class CatalogModelMemo
    {
        private readonly Dictionary<string, ConstructionKit.Contracts.DataTransferObjects.CkCompiledModelRoot?> _models = new();
        private readonly HashSet<string> _notPublished = new();

        public async Task<(bool found, ConstructionKit.Contracts.DataTransferObjects.CkCompiledModelRoot? model)>
            GetAsync(ICatalogService catalogService, CkModelId modelId, CancellationToken cancellationToken)
        {
            var key = modelId.FullName;
            if (_notPublished.Contains(key)) return (false, null);
            if (_models.TryGetValue(key, out var cached)) return (true, cached);

            try
            {
                var compiled = await catalogService.GetAsync(modelId, new OperationResult(),
                    cancellationToken: cancellationToken);
                _models[key] = compiled;
                return (true, compiled);
            }
            catch (ModelCatalogException)
            {
                _notPublished.Add(key);
                return (false, null);
            }
        }
    }

    private async Task<(bool isCompatible, string? reason)> CheckSystemCompatibilityAsync(
        CkModelId catalogModelId,
        Dictionary<string, CkVersion> installedSystemVersions,
        HashSet<string> visited,
        List<string> unresolvedDependencies,
        CatalogModelMemo memo,
        CancellationToken cancellationToken)
    {
        var (found, compiled) = await memo.GetAsync(_catalogService, catalogModelId, cancellationToken);
        if (!found)
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
                    // AB#7408: a range-retaining model (CK v2) declares the range it compiled against in
                    // DependencyRanges; any installed version inside the range (and at or above the floor)
                    // satisfies it. The exact pin in Dependencies is only the floor there.
                    var range = compiled.DependencyRanges?.FirstOrDefault(r => r.Range.Name == dep.Name);
                    if (range != null)
                    {
                        if (!range.IsSatisfiedBy(new CkModelId(dep.Name, installedVersion)))
                        {
                            return (false,
                                $"Requires {range.GetEffectiveRange().FullName}, but {dep.Name}-{installedVersion} is installed");
                        }
                    }
                    // Classic exact-pinned model: compiled models contain exact CkTypeId
                    // references (e.g. System-2.0.7/Entity-1) so the installed
                    // system version must match exactly.
                    else if (installedVersion.CompareTo(dep.Version) != 0)
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
                    dep, installedSystemVersions, visited, unresolvedDependencies, memo, cancellationToken);
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
        InstalledSnapshot installed,
        ITenantContext tenantContext,
        IReadOnlyCollection<CkModelId> availableVersions,
        CatalogModelMemo memo,
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
                    unresolvedDeps, memo, cancellationToken);
            }

            var hasInconsistency = unresolvedDeps.Count > 0;

            // AB#6868: tenant upgrade guard (read-only). Only an offered update that is otherwise importable can be
            // blocked; a blocked row keeps HasUpdate and loses NeedsAction, so a client that predates the guard
            // (octo-cli FixAll) skips it.
            // AB#7017: the row is judged with the dependency updates its import pulls in (the set the import dialog
            // judges), not the offered version alone.
            var blockers = new List<CkUpgradeBlockerDto>();
            if (hasUpdate && isCompatible && !hasInconsistency)
            {
                var updateSet = await ResolveUpdateSetAsync(catalog!.ModelId, installed, memo,
                    cancellationToken);
                blockers = await EvaluateBlockersAsync(tenantContext, updateSet, availableVersions, memo,
                    cancellationToken);
            }

            var isBlocked = blockers.Count > 0;
            var needsAction = (isResolveFailed || hasUpdate) && !isServiceManaged
                                                            && isCompatible && !hasInconsistency && !isBlocked;

            return new CkModelLibraryStatusItemDto
            {
                Name = inst.ModelId,
                InstalledVersion = inst.Id.Version.ToString(),
                ModelState = modelState,
                ResolveFailedReason = isResolveFailed ? inst.ResolveFailedReason : null,
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
                HasCatalogInconsistency = hasInconsistency,
                IsBlocked = isBlocked,
                Blockers = blockers,
                IsRangeRetaining = inst.DependencyRanges is { Length: > 0 }
            };
        }
        catch (Exception ex)
        {
            return new CkModelLibraryStatusItemDto
            {
                Name = inst.ModelId,
                InstalledVersion = inst.Id.Version.ToString(),
                ModelState = modelState,
                ResolveFailedReason = inst.ModelState ==
                                      ConstructionKit.Contracts.DataTransferObjects.ModelState.ResolveFailed
                    ? inst.ResolveFailedReason
                    : null,
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
        CatalogModelMemo memo,
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
                    unresolvedDeps, memo, cancellationToken);
            }

            // AB#7408: report range retention of the catalog model (memoized, already fetched by the check).
            var (found, compiled) = await memo.GetAsync(_catalogService, cm.ModelId, cancellationToken);

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
                HasCatalogInconsistency = unresolvedDeps.Count > 0,
                IsRangeRetaining = found && compiled is { IsRangeRetaining: true }
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
