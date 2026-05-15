using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Blueprints;

/// <summary>
/// GraphQL projection of <see cref="BlueprintDto"/>. Surfaces a single blueprint listing entry
/// — fully qualified id, name, version, optional description and the originating catalog name.
/// Returned by the <c>list</c> and <c>search</c> queries on the <c>blueprints</c> root.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class BlueprintDtoType : ObjectGraphType<BlueprintDto>
{
    public BlueprintDtoType()
    {
        Name = "Blueprint";
        Description = "Blueprint listing entry surfaced from any configured catalog.";

        Field<NonNullGraphType<StringGraphType>>("id")
            .Description("Fully-qualified blueprint id (Name-Version), e.g. \"InfrastructureStarter-1.0.0\".")
            .Resolve(ctx => ctx.Source!.Id);

        Field<NonNullGraphType<StringGraphType>>("name")
            .Description("Blueprint name without the version suffix.")
            .Resolve(ctx => ctx.Source!.Name);

        Field<NonNullGraphType<StringGraphType>>("version")
            .Description("Blueprint version (SemVer).")
            .Resolve(ctx => ctx.Source!.Version);

        Field<StringGraphType>("description")
            .Description("Optional description.")
            .Resolve(ctx => ctx.Source!.Description);

        Field<NonNullGraphType<StringGraphType>>("catalogName")
            .Description("Name of the catalog this entry was found in (e.g. \"PublicGitHubBlueprintCatalog\").")
            .Resolve(ctx => ctx.Source!.CatalogName);
    }
}
