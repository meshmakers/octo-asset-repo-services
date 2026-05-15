using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Blueprints;

/// <summary>
/// GraphQL projection of <see cref="BlueprintCatalogDto"/>. One row of the configured blueprint
/// catalog list — used by the studio to render the catalog filter dropdown.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class BlueprintCatalogDtoType : ObjectGraphType<BlueprintCatalogDto>
{
    public BlueprintCatalogDtoType()
    {
        Name = "BlueprintCatalog";
        Description = "Configured blueprint catalog source (local, public GitHub, private GitHub).";

        Field<NonNullGraphType<StringGraphType>>("name")
            .Description("Catalog name, e.g. \"PublicGitHubBlueprintCatalog\".")
            .Resolve(ctx => ctx.Source!.Name);

        Field<NonNullGraphType<StringGraphType>>("description")
            .Description("Human-readable catalog description.")
            .Resolve(ctx => ctx.Source!.Description);
    }
}
