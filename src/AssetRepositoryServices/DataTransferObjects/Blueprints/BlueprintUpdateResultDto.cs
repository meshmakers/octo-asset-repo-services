namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

/// <summary>
///     Result of a blueprint update (REST <c>updates/apply</c>, status 200). Added by AB#6315: the
///     endpoint used to answer 204 without a body; success is still any 2xx, so existing clients
///     that ignore the body keep working.
/// </summary>
public class BlueprintUpdateResultDto
{
    /// <summary>Whether the update completed successfully.</summary>
    public bool Success { get; set; }

    /// <summary>Entities added.</summary>
    public int EntitiesAdded { get; set; }

    /// <summary>Entities whose attributes changed.</summary>
    public int EntitiesUpdated { get; set; }

    /// <summary>Locked entities re-applied without an attribute change.</summary>
    public int EntitiesUnchanged { get; set; }

    /// <summary>Entities deleted.</summary>
    public int EntitiesDeleted { get; set; }

    /// <summary>Entities skipped due to conflicts.</summary>
    public int EntitiesSkipped { get; set; }

    /// <summary>Warnings raised during the update.</summary>
    public List<string> Warnings { get; set; } = [];

    /// <summary>
    ///     Attributes the seed would have blanked, each with what happened to it
    ///     (<c>AppliedOnUpdate</c> false = tenant value kept, true = blanked on confirmation).
    ///     Nothing is silent.
    /// </summary>
    public List<BlueprintBlankedAttributeDto> BlankedAttributes { get; set; } = [];

    /// <summary>
    ///     Tenant-owned seed entities the update leaves untouched because the tenant still holds them
    ///     (AB#6454, engine AB#6383). Identity only, never attribute values. Counted in the skipped total.
    /// </summary>
    public List<BlueprintTenantOwnedEntityDto> TenantOwnedSkipped { get; set; } = [];

    /// <summary>
    ///     Tenant-owned seed entities the tenant deleted and the update does not bring back (AB#6454).
    ///     <c>EntityId</c> is <c>null</c>. Counted in the skipped total.
    /// </summary>
    public List<BlueprintTenantOwnedEntityDto> TenantOwnedStaysDeleted { get; set; } = [];

    /// <summary>
    ///     True when the CK models of the update would break at least one installed model (tenant upgrade guard,
    ///     AB#6871). In a preview or a dry run it announces that a real update is refused with
    ///     <c>409 CK_UPGRADE_BLOCKED</c> unless it is forced; <see cref="Blockers" /> lists what breaks. Read-only
    ///     information, nothing was written.
    /// </summary>
    public bool IsBlocked { get; set; }

    /// <summary>
    ///     The CK models (full ids) the tenant upgrade guard judged as one set (AB#6871): the floors of the blueprint
    ///     the tenant does not hold in a satisfying-or-newer version. Empty when the guard did not run.
    /// </summary>
    public List<string> CkUpdateSet { get; set; } = [];

    /// <summary>
    ///     The installed models the CK update set breaks (AB#6871; same entries as the library status, AB#6868):
    ///     the blockers a caller forced past, or those a dry run reports. Empty when nothing breaks.
    /// </summary>
    public List<CkModelCatalog.CkUpgradeBlockerDto> Blockers { get; set; } = [];
}
