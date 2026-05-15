using GraphQL.Types;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Blueprints;

/// <summary>
/// GraphQL input wrapping <see cref="BlueprintConflictResolutionInputDto"/>: one per-entity
/// conflict-resolution override row. The studio passes a list of these on
/// <c>applyBlueprintUpdate</c> when any per-entity override is needed; the resolver folds
/// them into the engine's dictionary keyed by <c>entityId</c>.
/// </summary>
internal sealed class BlueprintConflictResolutionInputType : InputObjectGraphType<BlueprintConflictResolutionInputDto>
{
    public BlueprintConflictResolutionInputType()
    {
        Name = "BlueprintConflictResolutionInput";
        Description = "Per-entity override for a blueprint update conflict.";

        Field<NonNullGraphType<StringGraphType>>("entityId")
            .Description("Runtime id of the conflicting entity. Matches the entityId surfaced by previewUpdate.");

        Field<NonNullGraphType<ConflictResolutionEnumType>>("resolution")
            .Description("Override resolution for this entity.");
    }
}
