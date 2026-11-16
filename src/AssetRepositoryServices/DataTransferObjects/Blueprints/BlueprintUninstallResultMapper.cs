using Meshmakers.Octo.Runtime.Contracts.Blueprints;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

/// <summary>
///     Projects the engine's <see cref="BlueprintUninstallResult" /> onto <see cref="BlueprintUninstallResultDto" />, the
///     same for REST and GraphQL (AB#8966).
/// </summary>
public static class BlueprintUninstallResultMapper
{
    /// <summary>Maps an uninstall result (or the result of its dry run).</summary>
    public static BlueprintUninstallResultDto ToDto(BlueprintUninstallResult result)
    {
        return new BlueprintUninstallResultDto
        {
            Success = result.Success,
            DryRun = result.DryRun,
            UninstalledBlueprintId = result.UninstalledBlueprintId?.FullName,
            EntitiesDeleted = result.EntitiesDeleted,
            DeletedEntities = result.DeletedEntities
                .Select(e => new BlueprintUninstallDeletedEntityDto
                {
                    Key = e.Key,
                    CkTypeId = e.CkTypeId,
                    EntityId = e.EntityId,
                    WellKnownName = e.WellKnownName,
                    TenantOwned = e.TenantOwned
                })
                .ToList(),
            TenantOwnedKept = result.TenantOwnedKept
                .Select(e => new BlueprintTenantOwnedKeptEntityDto
                {
                    Key = e.Key,
                    CkTypeId = e.CkTypeId,
                    EntityId = e.EntityId,
                    WellKnownName = e.WellKnownName,
                    Reason = e.Reason.ToString(),
                    ReferencedBy = e.ReferencedBy.ToList()
                })
                .ToList(),
            CascadedDependencies = result.CascadedDependencies.Select(d => d.FullName).ToList(),
            BlockingDependents = result.BlockingDependents.Select(d => d.FullName).ToList(),
            Warnings = result.Warnings.ToList()
        };
    }
}
