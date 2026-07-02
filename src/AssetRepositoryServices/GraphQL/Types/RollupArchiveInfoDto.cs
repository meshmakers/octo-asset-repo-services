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
    int PendingRecomputeRanges,
    // Resolution-aware series routing metadata (AB#4290) — bucket alignment + the per-column stored
    // aggregation functions, so a client can walk the resolution family and know each rung's grain
    // and function in one round-trip instead of one rollupQueryMetadata call per rollup.
    string BucketAlignment,
    // IANA reference time-zone (AB#4300 / decision O6) that aligns calendar bucket boundaries to
    // local wall-clock time. Null ⇒ UTC calendar boundaries. Only meaningful for the calendar
    // BucketAlignment variants; ignored for FixedSize. Surfaced so the studio can show operators
    // whether a rollup is DST-correct instead of leaving it invisible on the entity.
    string? ReferenceTimeZone,
    IReadOnlyList<RollupAggregationInfoDto> Aggregations);

/// <summary>
/// One aggregation spec of a rollup, projected for the <c>rollupsFor</c> family metadata (AB#4290):
/// the source column path and the stored aggregation function. For single-step rollups the
/// <see cref="SourcePath"/> is the logical CK attribute path; for cascade rollups it is the parent
/// rollup's physical storage column (use <c>rollupQueryMetadata</c> for the reversed logical path).
/// </summary>
internal sealed record RollupAggregationInfoDto(string SourcePath, string Function);
