using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;

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
    }
}
