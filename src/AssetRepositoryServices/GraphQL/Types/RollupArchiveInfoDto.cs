using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
/// One row of the <c>rollupsFor(archiveRtId)</c> GraphQL query (rollup-archives concept §9).
/// Carries the rollup's identity, schedule configuration, watermark, and freeze state — the
/// minimal projection a studio dashboard needs to show "this source archive has these rollups
/// running, at this lag".
/// </summary>
internal sealed record RollupArchiveInfoDto(
    OctoObjectId RtId,
    string? RtWellKnownName,
    CkArchiveStatus Status,
    OctoObjectId SourceArchiveRtId,
    long BucketSizeMs,
    long WatermarkLagMs,
    DateTime? LastAggregatedBucketEnd,
    DateTime? FrozenUntil,
    int AggregationCount,
    // Recompute observability (AB#4184) — lets the studio show recompute health alongside the
    // rollup's schedule/watermark without a second query.
    bool RecomputeInProgress,
    DateTime? LastRecomputeStartedAt,
    DateTime? LastRecomputeSuccessAt,
    DateTime? LastRecomputeFailureAt,
    string? LastRecomputeFailureReason,
    int DirtyWindowsPending,
    int PendingRecomputeRanges);
