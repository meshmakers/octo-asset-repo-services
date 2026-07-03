using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

/// <summary>
/// GraphQL input for the <c>resolveSeriesQuery</c> query (AB#4290 resolution-aware series queries).
/// </summary>
internal sealed class ResolveSeriesQueryInputType : InputObjectGraphType<ResolveSeriesQueryInputDto>
{
    public ResolveSeriesQueryInputType()
    {
        Name = "ResolveSeriesQueryInput";
        Description = "Identifies a logical series (base archive + optional rtId/OBIS scope), a time window, a target point count and the required aggregation. The server picks the archive/rollup to query.";

        Field<NonNullGraphType<OctoObjectIdType>>("baseArchiveRtId")
            .Description("Runtime id of the base (raw / time-range) archive of the series' resolution family.");

        Field<NonNullGraphType<DateTimeGraphType>>("from")
            .Description("Inclusive start of the query window (UTC).");

        Field<NonNullGraphType<DateTimeGraphType>>("to")
            .Description("Exclusive end of the query window (UTC).");

        Field<NonNullGraphType<IntGraphType>>("targetPoints")
            .Description("Desired number of output points (pixel-driven, ~600 typical). Must be positive.");

        Field<NonNullGraphType<CkRollupFunctionGraphType>>("requiredAggregation")
            .Description("Aggregation the series must be reduced with (energy = SUM, demand = MAX, …). Never guessed; supplied by the caller.");

        Field<NonNullGraphType<StringGraphType>>("sourcePath")
            .Description("Logical CK attribute path of the measured column (e.g. Amount.Value) — used to match a rollup's aggregation spec.");

        Field<ListGraphType<NonNullGraphType<OctoObjectIdType>>>("rtIds")
            .Description("Optional source-entity rtId scope (e.g. the EnergyMeasurement entities of a MeteringPoint). Forwarded by the caller to the downsampling query.");

        Field<StringGraphType>("obisFilter")
            .Description("Optional OBIS-code filter narrowing the series. Forwarded by the caller to the downsampling query.");

        Field<StringGraphType>("timeZone")
            .Description("Optional IANA time zone (e.g. Europe/Vienna) the query is resolved in (AB#4190). Aligns calendar (day/week/month/year) rungs to that zone's DST-correct civil boundaries; null ⇒ UTC. Sub-day rungs are unaffected.");

        Field<SeriesComparisonPolicyGraphType>("comparisonPolicy")
            .Description("How civil boundaries are resolved across mixed-timezone series (AB#4190). PerQuery (default) applies the query timeZone uniformly; PerSeries aligns each series to its own archive reference time zone.");
    }
}

/// <summary>
/// GraphQL enum for <see cref="SeriesComparisonPolicy"/> (AB#4190). Constant-case names
/// (PER_QUERY / PER_SERIES) to match the schema's enum convention.
/// </summary>
internal sealed class SeriesComparisonPolicyGraphType : EnumerationGraphType<SeriesComparisonPolicy>
{
    public SeriesComparisonPolicyGraphType()
    {
        Name = "SeriesComparisonPolicy";
        Description = "Civil-boundary policy for a series query that spans multiple reference time zones (AB#4190).";
    }
}
