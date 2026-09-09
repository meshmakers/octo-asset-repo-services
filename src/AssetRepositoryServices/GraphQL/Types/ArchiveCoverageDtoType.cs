using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
/// GraphQL projection of <see cref="ArchiveCoverageDto"/>. Returned by the <c>coverageFor</c> query
/// (AB#5157) so the studio can show, per rung of an archive family, which time range actually holds
/// data — the same information the resolver's coverage filter uses to skip empty rungs.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class ArchiveCoverageDtoType : ObjectGraphType<ArchiveCoverageDto>
{
    public ArchiveCoverageDtoType()
    {
        Name = "ArchiveCoverageInfo";
        Description = "One rung of an archive family with its grain and MEASURED data coverage. availableFrom/availableTo are both null when the archive holds no data.";

        Field<NonNullGraphType<OctoObjectIdType>>("archiveRtId")
            .Description("Runtime id of the archive this row describes.")
            .Resolve(ctx => ctx.Source!.ArchiveRtId);

        Field<StringGraphType>("rtWellKnownName")
            .Description("Optional well-known name of the archive.")
            .Resolve(ctx => ctx.Source!.RtWellKnownName);

        Field<NonNullGraphType<BooleanGraphType>>("isBase")
            .Description("True for a raw / time-range archive (no rollup sources), false for a rollup rung.")
            .Resolve(ctx => ctx.Source!.IsBase);

        Field<NonNullGraphType<StringGraphType>>("status")
            .Description("Current lifecycle status: Created / Activated / Disabled / Failed.")
            .Resolve(ctx => ctx.Source!.Status.ToString());

        Field<LongGraphType>("bucketSizeMs")
            .Description("Bucket width in milliseconds for a rollup rung; null for a base archive.")
            .Resolve(ctx => ctx.Source!.BucketSizeMs);

        Field<NonNullGraphType<ArchiveCoverageBucketAlignmentGraphType>>("bucketAlignment")
            .Description("Bucket-boundary alignment of the rung (FixedSize for base archives).")
            .Resolve(ctx => ctx.Source!.BucketAlignment);

        Field<NonNullGraphType<ListGraphType<NonNullGraphType<CkRollupFunctionGraphType>>>>("storedFunctions")
            .Description("Aggregation functions stored on this rung; empty for a base archive.")
            .Resolve(ctx => ctx.Source!.StoredFunctions);

        Field<UtcDateTimeGraphType>("availableFrom")
            .Description("Earliest timestamp with data on this rung (measured). Null when the archive holds no data.")
            .Resolve(ctx => ctx.Source!.AvailableFrom);

        Field<UtcDateTimeGraphType>("availableTo")
            .Description("Latest timestamp with data on this rung (measured). Null when the archive holds no data.")
            .Resolve(ctx => ctx.Source!.AvailableTo);
    }
}

/// <summary>
/// Output GraphQL enum for <see cref="BucketAlignment"/> (AB#5157). Distinct from the input enum
/// <see cref="BucketAlignmentGraphType"/> (<c>BucketAlignmentInput</c>) because GraphQL forbids
/// sharing one enum name between input and output positions under different names.
/// </summary>
internal sealed class ArchiveCoverageBucketAlignmentGraphType : EnumerationGraphType<BucketAlignment>
{
    public ArchiveCoverageBucketAlignmentGraphType()
    {
        Name = "BucketAlignment";
        Description = "Bucket-boundary alignment of a rung: FIXED_SIZE / CALENDAR_DAY / ISO_8601_WEEK / CALENDAR_MONTH / CALENDAR_QUARTER / CALENDAR_YEAR.";
    }
}
