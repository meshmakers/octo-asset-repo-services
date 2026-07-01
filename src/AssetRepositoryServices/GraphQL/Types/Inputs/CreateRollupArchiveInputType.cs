using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

/// <summary>
/// GraphQL input for the <c>createRollupArchive</c> mutation. The studio sends only the rollup-
/// specific fields here; the server resolves <c>TargetCkTypeId</c> from the source archive and
/// derives the storage column list from the aggregations (concept §4 / §9). Avoids the duplication
/// of <see cref="Meshmakers.Octo.Runtime.Contracts.StreamData.RollupColumnGenerator"/>'s naming
/// rule in every client.
/// </summary>
internal sealed class CreateRollupArchiveInputType : InputObjectGraphType<CreateRollupArchiveInputDto>
{
    public CreateRollupArchiveInputType()
    {
        Name = "CreateRollupArchiveInput";
        Description = "Input for createRollupArchive: source archive + bucketing/lag + aggregations. TargetCkTypeId and Columns are resolved server-side.";

        Field<StringGraphType>("rtWellKnownName")
            .Description("Optional human-readable name for the rollup archive.");

        Field<NonNullGraphType<OctoObjectIdType>>("sourceArchiveRtId")
            .Description("Runtime id of the source archive (raw CkArchive or another CkRollupArchive for chained rollups).");

        Field<NonNullGraphType<LongGraphType>>("bucketSizeMs")
            .Description("Bucket width in milliseconds. Must be > 0.");

        Field<NonNullGraphType<LongGraphType>>("watermarkLagMs")
            .Description("Safety-wait after bucketEnd before aggregating, in milliseconds. >= 0.");

        Field<NonNullGraphType<ListGraphType<NonNullGraphType<RollupAggregationInputType>>>>("aggregations")
            .Description("Aggregation specs. At least one required; duplicate target column names are rejected.");
    }
}
