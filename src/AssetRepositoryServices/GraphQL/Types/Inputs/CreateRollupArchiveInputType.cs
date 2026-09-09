using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

/// <summary>
/// GraphQL input for the <c>createRollupArchive</c> mutation. The studio sends only the rollup-
/// specific fields here; the server resolves <c>TargetCkTypeId</c> from the source archive(s) and
/// derives the storage column list from the aggregations (concept §4 / §9). Avoids the duplication
/// of <see cref="Meshmakers.Octo.Runtime.Contracts.StreamData.RollupColumnGenerator"/>'s naming
/// rule in every client. Since AB#5157 the sources are declared through <c>sources</c>; the scalar
/// <c>sourceArchiveRtId</c> stays as a deprecated single-source shorthand.
/// </summary>
internal sealed class CreateRollupArchiveInputType : InputObjectGraphType<CreateRollupArchiveInputDto>
{
    public CreateRollupArchiveInputType()
    {
        Name = "CreateRollupArchiveInput";
        Description = "Input for createRollupArchive: source archive(s) + bucketing/lag + aggregations. TargetCkTypeId and Columns are resolved server-side. Supply either 'sources' (preferred) or the deprecated 'sourceArchiveRtId' — exactly one of the two.";

        Field<StringGraphType>("rtWellKnownName")
            .Description("Optional human-readable name for the rollup archive.");

        Field<OctoObjectIdType>("sourceArchiveRtId")
            .Description("DEPRECATED single-source shorthand: runtime id of the one source archive (raw CkArchive, TimeRangeArchive or another CkRollupArchive). Translated into one unbounded entry of 'sources'. Mutually exclusive with 'sources'.")
            .DeprecationReason("Use 'sources' (AB#5157): a rollup can aggregate from several source archives with validity spans. Kept as a single-unbounded-source shorthand.");

        Field<ListGraphType<NonNullGraphType<CreateRollupSourceInputType>>>("sources")
            .Description("Source archives of the rollup (AB#5157), each with an optional half-open validity span [validFrom, validTo). Spans must be pairwise disjoint, aligned to the rollup's bucket boundaries, and at most one source may be open-ended per direction. Required unless the deprecated 'sourceArchiveRtId' is supplied instead. An explicitly empty list is rejected — omit the field entirely to fall back to 'sourceArchiveRtId'.");

        Field<NonNullGraphType<LongGraphType>>("bucketSizeMs")
            .Description("Bucket width in milliseconds. Must be > 0.");

        Field<NonNullGraphType<LongGraphType>>("watermarkLagMs")
            .Description("Safety-wait after bucketEnd before aggregating, in milliseconds. >= 0.");

        Field<BucketAlignmentGraphType>("bucketAlignment")
            .Description("Optional bucket-boundary alignment. Defaults to FIXED_SIZE. Calendar variants (CALENDAR_DAY / ISO_8601_WEEK / CALENDAR_MONTH / CALENDAR_QUARTER / CALENDAR_YEAR) make day/week/month/quarter/year rollups expressible and are the only ones for which referenceTimeZone has any effect.")
            .DefaultValue(BucketAlignment.FixedSize);

        Field<StringGraphType>("referenceTimeZone")
            .Description("Optional IANA reference time-zone (e.g. 'Europe/Vienna') that aligns calendar bucket boundaries to local wall-clock time so they are DST-correct. Null keeps UTC boundaries. Ignored for FIXED_SIZE; an unknown zone id is rejected.");

        Field<LongGraphType>("carryLookbackMs")
            .Description("Optional bound on the TIME_WEIGHTED_AVG carry-in scan (last observation carried forward) in milliseconds. Null keeps the engine default of 35 days. Only meaningful when the aggregations include TIME_WEIGHTED_AVG; ignored otherwise. Must be > 0 when set.");

        Field<NonNullGraphType<ListGraphType<NonNullGraphType<RollupAggregationInputType>>>>("aggregations")
            .Description("Aggregation specs. At least one required; duplicate target column names are rejected.");
    }
}

/// <summary>
/// GraphQL input for one source declaration of a multi-source rollup (AB#5157). Mirrors
/// <see cref="RollupSourceReference"/>.
/// </summary>
internal sealed class CreateRollupSourceInputType : InputObjectGraphType<CreateRollupSourceInputDto>
{
    public CreateRollupSourceInputType()
    {
        Name = "CreateRollupSourceInput";
        Description = "One source archive of a rollup plus its optional validity span. validFrom is inclusive, validTo exclusive; both null = the source is authoritative for all time.";

        Field<NonNullGraphType<OctoObjectIdType>>("sourceArchiveRtId")
            .Description("Runtime id of the source archive (raw CkArchive, TimeRangeArchive or another CkRollupArchive).");

        Field<UtcDateTimeGraphType>("validFrom")
            .Description("Inclusive start of the validity span (ISO-8601, UTC). Null = unbounded towards the past. Must lie on a bucket boundary of the rollup.");

        Field<UtcDateTimeGraphType>("validTo")
            .Description("Exclusive end of the validity span (ISO-8601, UTC). Null = unbounded towards the future. Must lie on a bucket boundary of the rollup and be later than validFrom.");
    }
}

/// <summary>
/// GraphQL enum for <see cref="BucketAlignment"/> (AB#4300). Lets the studio send the same alignment
/// values it displays on the read side. The C# enum names map to CONSTANT_CASE (FixedSize →
/// FIXED_SIZE, CalendarDay → CALENDAR_DAY, CalendarQuarter → CALENDAR_QUARTER, …) via the default
/// enum-value converter.
/// </summary>
internal sealed class BucketAlignmentGraphType : EnumerationGraphType<BucketAlignment>
{
    public BucketAlignmentGraphType()
    {
        Name = "BucketAlignmentInput";
        Description = "Bucket-boundary alignment for a rollup archive: FIXED_SIZE / CALENDAR_DAY / ISO_8601_WEEK / CALENDAR_MONTH / CALENDAR_QUARTER / CALENDAR_YEAR.";
    }
}
