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
            .Description("Outcome classification (Ok / NoSuitableRollup / ResolutionLimited / UnknownBaseGrain / EmptyLadder).")
            .Resolve(ctx => ctx.Source!.Signal);

        Field<IntGraphType>("actualPoints")
            .Description("The deliverable point count when below the requested target (ResolutionLimited) or the native raw count on the refuse path. Null when the target was met.")
            .Resolve(ctx => ctx.Source!.ActualPoints);

        Field<StringGraphType>("diagnostic")
            .Description("Optional human-readable explanation of the chosen route / signal.")
            .Resolve(ctx => ctx.Source!.Diagnostic);
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
        Description = "Outcome of resolution-aware series routing. Non-Ok values are truthful signals the caller can surface — the resolver never silently produces a wrong or degraded result.";
    }
}
