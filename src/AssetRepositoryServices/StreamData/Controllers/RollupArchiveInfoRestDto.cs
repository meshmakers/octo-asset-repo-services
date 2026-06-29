namespace Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Controllers;

/// <summary>
/// REST projection of one rollup archive attached to a source CkArchive. Returned by
/// <c>GET streamdata/archives/{archiveRtId}/rollups</c>. Mirrors the GraphQL <c>rollupsFor</c>
/// payload so studio and CLI consumers see the same shape regardless of transport.
/// Rollup-archives concept §9.
/// </summary>
public sealed record RollupArchiveInfoRestDto(
    string RtId,
    string? RtWellKnownName,
    string Status,
    string SourceArchiveRtId,
    long BucketSizeMs,
    long WatermarkLagMs,
    DateTime? LastAggregatedBucketEnd,
    DateTime? FrozenUntil,
    int AggregationCount,
    // Recompute observability (AB#4184) — same fields as the GraphQL RollupArchiveInfo so CLI /
    // studio consumers see recompute health over either transport.
    bool RecomputeInProgress,
    DateTime? LastRecomputeStartedAt,
    DateTime? LastRecomputeSuccessAt,
    DateTime? LastRecomputeFailureAt,
    string? LastRecomputeFailureReason,
    int DirtyWindowsPending,
    int PendingRecomputeRanges);
