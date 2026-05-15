using GraphQL.Types;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Blueprints;

/// <summary>
/// GraphQL enum mirror of <see cref="BlueprintUpdateMode"/>. Picks how an update reconciles
/// the catalog blueprint against tenant state — Safe (add-only), Merge (add + upsert locked),
/// Full (also delete orphans), or Migration (run the script).
/// </summary>
internal sealed class BlueprintUpdateModeEnumType : EnumerationGraphType<BlueprintUpdateMode>
{
    public BlueprintUpdateModeEnumType()
    {
        Name = "BlueprintUpdateMode";
        Description = "How a blueprint update reconciles seed data with tenant state.";
    }
}
