using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Blueprints;

/// <summary>
/// GraphQL projection of <see cref="BlueprintListResponseDto"/>. Page of blueprints plus
/// the original paging cursor so clients can render skip/take controls without bookkeeping
/// state of their own.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class BlueprintListResponseDtoType : ObjectGraphType<BlueprintListResponseDto>
{
    public BlueprintListResponseDtoType()
    {
        Name = "BlueprintListResponse";
        Description = "Paged list of blueprints from the configured catalogs.";

        Field<NonNullGraphType<ListGraphType<NonNullGraphType<BlueprintDtoType>>>>("items")
            .Description("Page of blueprint entries.")
            .Resolve(ctx => ctx.Source!.Items);

        Field<NonNullGraphType<IntGraphType>>("totalCount")
            .Description("Total number of blueprints available across all queried catalogs.")
            .Resolve(ctx => ctx.Source!.TotalCount);

        Field<NonNullGraphType<IntGraphType>>("skip")
            .Description("Number of items skipped before this page.")
            .Resolve(ctx => ctx.Source!.Skip);

        Field<NonNullGraphType<IntGraphType>>("take")
            .Description("Page size used to produce this response.")
            .Resolve(ctx => ctx.Source!.Take);
    }
}
