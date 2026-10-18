using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services;

/// <summary>
///     The asset repository's tenant upgrade guard for blueprints (CK v2 F2.5, AB#6871): judges the CK models a
///     blueprint apply or update installs or raises as ONE set, with the same evaluation the library status
///     (AB#6868) and the import endpoints (AB#6870) use, so a blueprint is refused for exactly the reasons an import
///     of the same models would be. Read-only.
/// </summary>
/// <remarks>
///     Service-managed models (<c>System</c>, <c>System.*</c>) are not judged: the platform owns them and the embedded
///     import has its own downgrade guard. A model that is in no catalog cannot be judged and is left out, as for an
///     import.
/// </remarks>
internal sealed class BlueprintCkUpgradeGuard : IBlueprintCkUpgradeGuard
{
    private readonly ICkModelLibraryStatusService _libraryStatusService;

    public BlueprintCkUpgradeGuard(ICkModelLibraryStatusService libraryStatusService)
    {
        _libraryStatusService = libraryStatusService;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BlueprintCkUpgradeBlocker>> EvaluateAsync(string tenantId,
        IReadOnlyList<CkModelId> updateSet, CancellationToken cancellationToken = default)
    {
        var judged = updateSet.Where(m => !CkModelLibraryStatusService.IsSystemManaged(m.Name)).ToList();
        if (judged.Count == 0)
        {
            return [];
        }

        var blockers = await _libraryStatusService.EvaluateUpdateSetBlockersAsync(tenantId, judged, cancellationToken);
        return blockers.Select(ToBlueprintBlocker).ToList();
    }

    private static BlueprintCkUpgradeBlocker ToBlueprintBlocker(CkUpgradeBlockerDto dto) => new()
    {
        Dependent = dto.Dependent,
        Update = dto.Update,
        Kind = dto.Kind,
        Subject = dto.Subject,
        Code = dto.Code,
        Message = dto.Message,
        DependentIsInUpdateSet = dto.DependentIsInUpdateSet,
        SuggestedDependentUpdate = dto.SuggestedDependentUpdate,
        ResolveHint = dto.ResolveHint
    };
}
