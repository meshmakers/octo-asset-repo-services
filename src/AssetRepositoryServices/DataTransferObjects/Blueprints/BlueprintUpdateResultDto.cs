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
}
