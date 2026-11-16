namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

/// <summary>
///     Result of a blueprint uninstall request, or of its dry run (AB#8966).
/// </summary>
public class BlueprintUninstallResultDto
{
    /// <summary>True when the uninstall (or its dry run) completed.</summary>
    public bool Success { get; set; }

    /// <summary>AB#8966: true for a dry run - nothing was changed, the counts and lists say what the uninstall would do.</summary>
    public bool DryRun { get; set; }

    /// <summary>Fully-qualified id of the blueprint that was uninstalled, if any.</summary>
    public string? UninstalledBlueprintId { get; set; }

    /// <summary>
    ///     Number of entities erased from the tenant: the locked entities of the blueprint and its unedited, unreferenced
    ///     tenant-owned seed entities (AB#8966). In a dry run: the number that would be erased.
    /// </summary>
    public int EntitiesDeleted { get; set; }

    /// <summary>AB#8966: the entities counted in <see cref="EntitiesDeleted" />, cascaded blueprints included.</summary>
    public List<BlueprintUninstallDeletedEntityDto> DeletedEntities { get; set; } = [];

    /// <summary>
    ///     AB#8966: tenant-owned seed entities of the blueprint that stay in the tenant, each with the reason
    ///     (<c>Edited</c>, <c>Referenced</c>, <c>NotComparable</c> or <c>EraseFailed</c>).
    /// </summary>
    public List<BlueprintTenantOwnedKeptEntityDto> TenantOwnedKept { get; set; } = [];

    /// <summary>Blueprint ids that were cascade-uninstalled.</summary>
    public List<string> CascadedDependencies { get; set; } = [];

    /// <summary>
    ///     Other installed blueprints that still depend on the target. Populated
    ///     when uninstall is blocked because cascade was not requested.
    /// </summary>
    public List<string> BlockingDependents { get; set; } = [];

    /// <summary>Warnings reported during the operation.</summary>
    public List<string> Warnings { get; set; } = [];
}

/// <summary>
///     AB#8966: an entity an uninstall erased (or, in a dry run, would erase). Identity only, never attribute values.
/// </summary>
public class BlueprintUninstallDeletedEntityDto : BlueprintTenantOwnedEntityDto
{
    /// <summary>
    ///     True for a tenant-owned seed entity erased because the tenant never edited it; false for a locked entity of the
    ///     blueprint.
    /// </summary>
    public bool TenantOwned { get; set; }
}

/// <summary>
///     AB#8966: a tenant-owned seed entity an uninstall keeps, with the reason. Identity only, never attribute values.
/// </summary>
public class BlueprintTenantOwnedKeptEntityDto : BlueprintTenantOwnedEntityDto
{
    /// <summary>
    ///     Why the entity is kept: <c>Edited</c> (the tenant changed it), <c>Referenced</c> (an entity that stays shares an
    ///     association with it), <c>NotComparable</c> (whether it was edited cannot be decided) or <c>EraseFailed</c> (the
    ///     erase failed; the reason is in the warnings).
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>For <c>Referenced</c>: the runtime ids of the entities that keep it. Empty otherwise.</summary>
    public List<string> ReferencedBy { get; set; } = [];
}
