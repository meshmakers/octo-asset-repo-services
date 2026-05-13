using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
/// GraphQL projection of <see cref="RollupQueryMetadataDto"/>. Returned by the
/// <c>rollupQueryMetadata</c> query — gives the studio everything it needs to build the
/// stream-data column / aggregation pickers for a rollup archive in a single round-trip.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class RollupQueryMetadataDtoType : ObjectGraphType<RollupQueryMetadataDto>
{
    public RollupQueryMetadataDtoType()
    {
        Name = "RollupQueryMetadata";
        Description = "Per-rollup metadata the studio's stream-data query editor needs (bucket size, logical attribute paths derived via chain walking).";

        Field<NonNullGraphType<OctoObjectIdType>>("rtId")
            .Description("Runtime id of the rollup archive — echo of the request argument.")
            .Resolve(ctx => ctx.Source!.RtId);

        Field<NonNullGraphType<LongGraphType>>("bucketSizeMs")
            .Description("Native bucket size of this rollup in milliseconds. Drives the downsampling bucket-alignment warning.")
            .Resolve(ctx => ctx.Source!.BucketSizeMs);

        Field<NonNullGraphType<ListGraphType<NonNullGraphType<StringGraphType>>>>("logicalSourcePaths")
            .Description("Distinct logical CK-attribute paths the rollup aggregates over. For cascade rollups these are derived via chain walking (RollupLogicalPathResolver).")
            .Resolve(ctx => ctx.Source!.LogicalSourcePaths);
    }
}
