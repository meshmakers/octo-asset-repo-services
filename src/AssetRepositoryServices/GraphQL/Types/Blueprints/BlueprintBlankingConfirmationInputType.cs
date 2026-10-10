using GraphQL.Types;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Blueprints;

/// <summary>
/// GraphQL input confirming that one attribute of one entity may be blanked by a blueprint update
/// (AB#6315). Take the values from <c>previewUpdate.blankedAttributes</c>.
/// </summary>
internal sealed class BlueprintBlankingConfirmationInputType : InputObjectGraphType<BlueprintBlankingConfirmationInputDto>
{
    public BlueprintBlankingConfirmationInputType()
    {
        Name = "BlueprintBlankingConfirmationInput";
        Description = "Explicit confirmation that one attribute of one entity may be blanked by the update.";

        Field<NonNullGraphType<StringGraphType>>("rtId")
            .Description("Runtime id of the entity, as listed in previewUpdate.blankedAttributes.");

        Field<NonNullGraphType<StringGraphType>>("attributeName")
            .Description("Attribute name (case-insensitive), as listed in previewUpdate.blankedAttributes.");
    }
}
