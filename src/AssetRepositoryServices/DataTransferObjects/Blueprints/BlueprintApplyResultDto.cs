namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

/// <summary>
///     Result of a blueprint apply operation.
/// </summary>
public class BlueprintApplyResultDto
{
    /// <summary>Whether the apply completed successfully.</summary>
    public bool Success { get; set; }

    /// <summary>Tenant the blueprint was applied to.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Fully-qualified blueprint id that was applied.</summary>
    public string BlueprintId { get; set; } = string.Empty;

    /// <summary>Application mode used: Initial or ReApply.</summary>
    public string ApplicationMode { get; set; } = string.Empty;

    /// <summary>Number of seed-data files that were imported.</summary>
    public int SeedDataFilesApplied { get; set; }

    /// <summary>CK models loaded as part of the application.</summary>
    public List<string> LoadedCkModels { get; set; } = [];

    /// <summary>Warnings raised during the operation.</summary>
    public List<string> Warnings { get; set; } = [];

    /// <summary>
    ///     Attributes a blueprint update would have blanked and what happened to each (AB#6315);
    ///     empty for an install.
    /// </summary>
    public List<BlueprintBlankedAttributeDto> BlankedAttributes { get; set; } = [];

    /// <summary>
    ///     Tenant-owned seed entities an update leaves untouched because the tenant still holds them
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
