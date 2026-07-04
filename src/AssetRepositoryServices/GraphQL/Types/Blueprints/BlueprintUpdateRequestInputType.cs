using GraphQL.Types;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Blueprints;

/// <summary>
/// GraphQL input for <c>previewBlueprintUpdate</c> and <c>applyBlueprintUpdate</c>.
/// Mirrors the REST <c>BlueprintUpdateRequestDto</c> but uses typed enums and a per-entity
/// override list instead of a dictionary (GraphQL has no native map type).
/// </summary>
internal sealed class BlueprintUpdateRequestInputType : InputObjectGraphType<BlueprintUpdateRequestInputDto>
{
    public BlueprintUpdateRequestInputType()
    {
        Name = "BlueprintUpdateRequestInput";
        Description = "Parameters for previewing or applying a blueprint update on the tenant.";

        Field<NonNullGraphType<StringGraphType>>("targetVersion")
            .Description("Fully-qualified target blueprint id (Name-Version), e.g. \"InfrastructureStarter-2.0.0\".");

        Field<BlueprintUpdateModeEnumType>("updateMode")
            .Description("Update reconciliation mode. Defaults to Merge.");

        Field<BooleanGraphType>("dryRun")
            .Description("Compute the diff without persisting any changes.");

        Field<ListGraphType<NonNullGraphType<BlueprintConflictResolutionInputType>>>("conflictResolutions")
            .Description("Per-entity overrides for conflicts surfaced by previewUpdate.");
    }
}
