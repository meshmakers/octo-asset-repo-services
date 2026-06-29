using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
/// GraphQL projection of <see cref="RollupArchiveInfoDto"/>. Surfaces one rollup attached to a
/// source archive plus its schedule, watermark, and freeze state — minimal data for a studio
/// dashboard. Returned by the <c>rollupsFor</c> query (rollup-archives concept §9).
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class RollupArchiveInfoDtoType : ObjectGraphType<RollupArchiveInfoDto>
{
    public RollupArchiveInfoDtoType()
    {
        Name = "RollupArchiveInfo";
        Description = "Rollup archive attached to a source CkArchive, with its schedule and current watermark.";

        Field<NonNullGraphType<OctoObjectIdType>>("rtId")
            .Description("Runtime id of the rollup archive.")
            .Resolve(ctx => ctx.Source!.RtId);

        Field<StringGraphType>("rtWellKnownName")
            .Description("Optional well-known name of the rollup archive.")
            .Resolve(ctx => ctx.Source!.RtWellKnownName);

        Field<NonNullGraphType<StringGraphType>>("status")
            .Description("Current lifecycle status: Created / Activated / Disabled / Failed.")
            .Resolve(ctx => ctx.Source!.Status.ToString());

        Field<NonNullGraphType<OctoObjectIdType>>("sourceArchiveRtId")
            .Description("Runtime id of the source archive this rollup aggregates from.")
            .Resolve(ctx => ctx.Source!.SourceArchiveRtId);

        Field<NonNullGraphType<LongGraphType>>("bucketSizeMs")
            .Description("Bucket width in milliseconds.")
            .Resolve(ctx => ctx.Source!.BucketSizeMs);

        Field<NonNullGraphType<LongGraphType>>("watermarkLagMs")
            .Description("Watermark lag in milliseconds — how far behind real-time the orchestrator stays before closing a bucket.")
            .Resolve(ctx => ctx.Source!.WatermarkLagMs);

        Field<DateTimeGraphType>("lastAggregatedBucketEnd")
            .Description("Exclusive end timestamp of the most recently committed bucket. Null before the first orchestrator tick.")
            .Resolve(ctx => ctx.Source!.LastAggregatedBucketEnd);

        Field<DateTimeGraphType>("frozenUntil")
            .Description("Upper bound of the frozen range, if set. Buckets ending at or before this point are not re-aggregated by the orchestrator.")
            .Resolve(ctx => ctx.Source!.FrozenUntil);

        Field<NonNullGraphType<IntGraphType>>("aggregationCount")
            .Description("Number of aggregation specs configured on this rollup.")
            .Resolve(ctx => ctx.Source!.AggregationCount);

        // ---------- Recompute observability (AB#4184) ----------
        Field<NonNullGraphType<BooleanGraphType>>("recomputeInProgress")
            .Description("True while a recompute job for this rollup is running or swapping.")
            .Resolve(ctx => ctx.Source!.RecomputeInProgress);

        Field<DateTimeGraphType>("lastRecomputeStartedAt")
            .Description("Start timestamp of the most recent recompute run. Null before the first run.")
            .Resolve(ctx => ctx.Source!.LastRecomputeStartedAt);

        Field<DateTimeGraphType>("lastRecomputeSuccessAt")
            .Description("Finish timestamp of the most recent successfully committed recompute run. Null before the first success.")
            .Resolve(ctx => ctx.Source!.LastRecomputeSuccessAt);

        Field<DateTimeGraphType>("lastRecomputeFailureAt")
            .Description("Timestamp of the most recent failed recompute run. Null if the last run succeeded.")
            .Resolve(ctx => ctx.Source!.LastRecomputeFailureAt);

        Field<StringGraphType>("lastRecomputeFailureReason")
            .Description("Human-readable reason for the most recent recompute failure. Null if the last run succeeded.")
            .Resolve(ctx => ctx.Source!.LastRecomputeFailureReason);

        Field<NonNullGraphType<IntGraphType>>("dirtyWindowsPending")
            .Description("Number of dirty windows recorded on this archive (retroactive changes not yet propagated). 0 in the steady state.")
            .Resolve(ctx => ctx.Source!.DirtyWindowsPending);

        Field<NonNullGraphType<IntGraphType>>("pendingRecomputeRanges")
            .Description("Number of pending recompute ranges queued on this archive (the recompute work list still to drain). 0 in the steady state.")
            .Resolve(ctx => ctx.Source!.PendingRecomputeRanges);
    }
}
