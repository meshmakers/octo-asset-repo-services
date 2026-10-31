using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;
using Meshmakers.Octo.ConstructionKit.Contracts;
using CkCompiledModelRoot = Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects.CkCompiledModelRoot;
using CkModelDependencyDto = Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects.CkModelDependencyDto;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services;

/// <summary>
///     The single calculation of "which models does importing this catalog model install or raise": the dependency
///     tree of the offered version compared with what the tenant has installed, flattened to the import set (AB#7017).
///     It was extracted unchanged from <c>ModelsController</c> so that the import dialog (<c>ResolveDependencies</c>,
///     <c>ResolveDependenciesBatch</c>) and the library status rows judge exactly the same set - the row and the
///     dialog cannot disagree about what an update pulls in. Read-only; nothing here touches the tenant.
/// </summary>
internal static class CkImportDependencyResolver
{
    /// <summary>Loads the compiled model of a catalog dependency; <c>null</c> when it is not published.</summary>
    internal delegate Task<CkCompiledModelRoot?> ModelLoader(CkModelId modelId, CancellationToken cancellationToken);

    /// <summary>True when exactly this model version is installed in the tenant.</summary>
    internal delegate Task<bool> InstalledCheck(CkModelId modelId);

    /// <summary>
    ///     What the parent model requires of a dependency: the exact compile-time pin of a classic model, or the declared
    ///     range + floor of a range-retaining model (CK v2 F1.0 / G3 review A-M1).
    /// </summary>
    internal sealed record DependencyRequirement(CkModelId Pin, CkModelDependencyDto? Range)
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

    /// <summary>Resolves the tree of the offered root model (the root is judged against its own exact version).</summary>
    internal static Task<DependencyResolutionItemDto> ResolveRootAsync(CkCompiledModelRoot root,
        InstalledCheck isInstalled, IReadOnlyDictionary<string, CkVersion> installedVersions, ModelLoader loadModel,
        CancellationToken cancellationToken) =>
        ResolveDependencyTreeAsync(root.ModelId, DependencyRequirement.Exact(root.ModelId), root, isInstalled,
            installedVersions, new HashSet<string>(), loadModel, cancellationToken);

    internal static async Task<DependencyResolutionItemDto> ResolveDependencyTreeAsync(
        CkModelId modelId,
        DependencyRequirement requirement,
        CkCompiledModelRoot? compiledModel,
        InstalledCheck isInstalledCheck,
        IReadOnlyDictionary<string, CkVersion> installedVersions,
        HashSet<string> resolved,
        ModelLoader loadModel,
        CancellationToken cancellationToken)
    {
        var item = new DependencyResolutionItemDto
        {
            ModelId = modelId.FullName,
            Name = modelId.Name,
            RequiredVersion = modelId.Version.ToString(),
            RequiredRange = requirement.Range?.ToString()
        };

        var isServiceManaged = CkModelLibraryStatusService.IsSystemManaged(modelId.Name);
        var isInstalled = await isInstalledCheck(modelId);
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
                // Prevent circular dependencies. Keyed by the requirement too (re-review N8): a dependency shared by
                // a range-retaining and a classic parent must be judged against each requirement, otherwise the
                // classic parent's exact pin would never be checked.
                var requirementForDep = DependencyRequirement.For(dep, compiledModel);

                // AB#7940: a range-retaining model lists the flat exact closure in Dependencies (including the
                // transitive pins, e.g. System-2.7.0 via Basic) but its dependencyRanges name only the direct
                // dependencies. A dependency without a range entry is therefore transitive: it is not judged at this
                // level (and not added to 'resolved'); it is judged when the intermediate model that really
                // requires it is resolved, with that model's own range (range-retaining) or exact pin (classic).
                if (compiledModel.IsRangeRetaining && requirementForDep.Range == null)
                {
                    continue;
                }

                if (!resolved.Add($"{dep.FullName}|{requirementForDep.Describe()}"))
                {
                    continue;
                }

                // Fetch sub-dependency from catalog to get its dependencies
                var depModel = await loadModel(dep, cancellationToken);

                var depItem = await ResolveDependencyTreeAsync(dep, requirementForDep, depModel, isInstalledCheck,
                    installedVersions, resolved, loadModel, cancellationToken);
                item.Dependencies.Add(depItem);
            }
        }

        return item;
    }

    internal static void CorrectTreeActions(DependencyResolutionItemDto item,
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
            // The typed id, never the display strings (re-review N1).
            var itemVersion = new CkModelId(item.ModelId).Version;
            if (importVersion.Version.CompareTo(itemVersion) > 0)
            {
                item.Action = "none";
                item.InstalledVersion = $"(will import {importVersion.FullName})";
            }
        }
    }

    /// <summary>
    ///     Flattens a resolved tree into the models the import installs or raises, dependencies first. A branch with
    ///     an incompatible dependency is left out; a model that appears twice keeps its highest version.
    /// </summary>
    internal static void CollectModelsToImport(DependencyResolutionItemDto item, List<string> result,
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

    private static bool HasIncompatibleDependency(DependencyResolutionItemDto item)
    {
        if (item.Action == "incompatible") return true;
        return item.Dependencies.Any(HasIncompatibleDependency);
    }
}
