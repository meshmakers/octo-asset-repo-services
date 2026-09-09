using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
/// GraphQL projection of <see cref="ResolveSeriesQueryDto"/>. Returned by the
/// <c>resolveSeriesQuery</c> query — the archive-selection decision for a resolution-aware series
/// query. Null when StreamData is not enabled for the tenant.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class ResolveSeriesQueryDtoType : ObjectGraphType<ResolveSeriesQueryDto>
{
    public ResolveSeriesQueryDtoType()
    {
        Name = "ResolveSeriesQueryResult";
        Description = "Archive-selection decision for a resolution-aware series query: which archive to query, the effective bucket width, expected point count, reducer, and an outcome signal.";

        Field<NonNullGraphType<OctoObjectIdType>>("archiveRtId")
            .Description("The archive to query — a rollup, or the base archive on the refuse/raw paths.")
            .Resolve(ctx => ctx.Source!.ArchiveRtId);

        Field<NonNullGraphType<LongGraphType>>("effectiveBucketMs")
            .Description("Width in milliseconds of one output bucket; 0 when no bucketing applies / grain unknown.")
            .Resolve(ctx => ctx.Source!.EffectiveBucketMs);

        Field<NonNullGraphType<IntGraphType>>("points")
            .Description("Number of points the caller can expect from the downsampling query.")
            .Resolve(ctx => ctx.Source!.Points);

        Field<NonNullGraphType<CkRollupFunctionGraphType>>("reducingFunction")
            .Description("Aggregation function the downsampling query must use.")
            .Resolve(ctx => ctx.Source!.ReducingFunction);

        Field<NonNullGraphType<SeriesResolutionSignalGraphType>>("signal")
            .Description("Outcome classification (Ok / NoSuitableRollup / ResolutionLimited / UnknownBaseGrain / EmptyLadder / CoverageLimited). CoverageLimited (AB#5157) means the coverage filter redirected the query to a coarser rung because the finer rung holds no data for the requested window; finerRungAvailableFrom says from when the finer rung could serve.")
            .Resolve(ctx => ctx.Source!.Signal);

        Field<IntGraphType>("actualPoints")
            .Description("The deliverable point count when below the requested target (ResolutionLimited / CoverageLimited) or the native raw count on the refuse path. Null when the target was met.")
            .Resolve(ctx => ctx.Source!.ActualPoints);

        Field<StringGraphType>("diagnostic")
            .Description("Optional human-readable explanation of the chosen route / signal. On CoverageLimited (and on a refuse signal after a coverage exclusion) it names the excluded rung.")
            .Resolve(ctx => ctx.Source!.Diagnostic);

        Field<UtcDateTimeGraphType>("finerRungAvailableFrom")
            .Description("Earliest timestamp from which the finer rung excluded by the coverage filter would be usable (AB#5157). Set only when signal is CoverageLimited; null otherwise.")
            .Resolve(ctx => ctx.Source!.FinerRungAvailableFrom);
    }
}

/// <summary>
/// GraphQL enum for <see cref="SeriesResolutionSignal"/>, using the C# enum names directly.
/// </summary>
internal sealed class SeriesResolutionSignalGraphType : EnumerationGraphType<SeriesResolutionSignal>
{
    public SeriesResolutionSignalGraphType()
    {
        Name = "SeriesResolutionSignal";
        Description = "Outcome of resolution-aware series routing. Non-Ok values are truthful signals the caller can surface — the resolver never silently produces a wrong or degraded result. CoverageLimited (AB#5157): a finer rung was skipped for lacking measured coverage over the requested window.";
    }
}
