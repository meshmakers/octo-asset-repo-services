namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

/// <summary>
///     Preview of changes that would be applied by a blueprint update
/// </summary>
public class BlueprintUpdatePreviewDto
{
    /// <summary>
    ///     Target version to update to
    /// </summary>
    public string TargetVersion { get; set; } = string.Empty;

    /// <summary>
    ///     Number of entities that would be added
    /// </summary>
    public int EntitiesToAdd { get; set; }

    /// <summary>
    ///     Number of entities that would be updated
    /// </summary>
    public int EntitiesToUpdate { get; set; }

    /// <summary>
    ///     Number of entities that would be deleted
    /// </summary>
    public int EntitiesToDelete { get; set; }

    /// <summary>
    ///     List of conflicts that would occur
    /// </summary>
    public List<BlueprintConflictDto> Conflicts { get; set; } = [];

    /// <summary>
    ///     List of warnings
    /// </summary>
    public List<string> Warnings { get; set; } = [];

    /// <summary>
    ///     Number of blueprint-managed entities the update would re-apply without changing a single
    ///     attribute (AB#5297). <see cref="EntitiesToUpdate" /> + this = the managed entities that
    ///     exist on the tenant.
    /// </summary>
    public int EntitiesUnchanged { get; set; }

    /// <summary>
    ///     One entry per entity counted in <see cref="EntitiesToUpdate" />: which attributes the
    ///     update would change, with the tenant's current value and the seed's value. This is the
    ///     list an operator reads before touching a production tenant (AB#5297, AB#5308).
    /// </summary>
    public List<BlueprintEntityChangeDto> Changes { get; set; } = [];
}

/// <summary>
///     An entity the update would change, with its attribute-level diff.
/// </summary>
public class BlueprintEntityChangeDto
{
    /// <summary>
    ///     Runtime id of the entity.
    /// </summary>
    public string EntityId { get; set; } = string.Empty;

    /// <summary>
    ///     Well-known name of the entity, when the seed assigns one.
    /// </summary>
    public string? EntityWellKnownName { get; set; }

    /// <summary>
    ///     Display name of the entity on the tenant, when it has one.
    /// </summary>
    public string? EntityDisplayName { get; set; }

    /// <summary>
    ///     Construction-kit type of the entity.
    /// </summary>
    public string EntityCkTypeId { get; set; } = string.Empty;

    /// <summary>
    ///     The attributes that differ. Empty when <see cref="Note" /> explains why the entity is
    ///     counted without an attribute list.
    /// </summary>
    public List<BlueprintAttributeChangeDto> Attributes { get; set; } = [];

    /// <summary>
    ///     Set when the engine could not compare the entity attribute by attribute (type not in the
    ///     CK cache, conversion failure) and counts it as changed to stay on the safe side.
    /// </summary>
    public string? Note { get; set; }
}

/// <summary>
///     One attribute the update would change.
/// </summary>
public class BlueprintAttributeChangeDto
{
    /// <summary>
    ///     Attribute name as declared on the CK type.
    /// </summary>
    public string AttributeName { get; set; } = string.Empty;

    /// <summary>
    ///     The tenant's current value, rendered as text: scalars verbatim, records and lists as JSON.
    ///     <c>null</c> when the tenant has no value.
    /// </summary>
    public string? OldValue { get; set; }

    /// <summary>
    ///     The seed's value the update would write, rendered the same way. <c>null</c> when the
    ///     seed clears the attribute.
    /// </summary>
    public string? NewValue { get; set; }
}

/// <summary>
///     Represents a conflict during blueprint update
/// </summary>
public class BlueprintConflictDto
{
    /// <summary>
    ///     Entity ID that has a conflict
    /// </summary>
    public string EntityId { get; set; } = string.Empty;

    /// <summary>
    ///     Description of the conflict
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    ///     Suggested resolution
    /// </summary>
    public string? SuggestedResolution { get; set; }
}
