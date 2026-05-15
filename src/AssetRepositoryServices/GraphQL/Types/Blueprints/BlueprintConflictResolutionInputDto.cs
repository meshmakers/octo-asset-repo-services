namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Blueprints;

/// <summary>
/// Wire-level representation of one per-entity conflict override for
/// <c>applyBlueprintUpdate</c>. GraphQL doesn't carry maps natively; we accept a list of
/// these and fold it into the engine's <see cref="System.Collections.Generic.Dictionary{TKey,TValue}"/>
/// keyed by <c>EntityId</c>.
/// </summary>
internal sealed class BlueprintConflictResolutionInputDto
{
    /// <summary>Runtime id of the conflicting entity (matches <c>BlueprintConflict.entityId</c>).</summary>
    public string EntityId { get; set; } = string.Empty;

    /// <summary>Resolution to apply for this entity: KeepUser / KeepBlueprint / Merge / Skip.</summary>
    public string Resolution { get; set; } = string.Empty;
}
