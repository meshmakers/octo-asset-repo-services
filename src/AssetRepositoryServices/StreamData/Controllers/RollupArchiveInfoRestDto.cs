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
    // Deprecated single-source projection (AB#5157): set only when the rollup declares exactly one
    // unbounded source; null for multi-source rollups. Consumers read Sources.
    string? SourceArchiveRtId,
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
    int PendingRecomputeRanges,
    // Multi-source declaration (AB#5157): every source archive with its half-open validity span.
    IReadOnlyList<RollupSourceRestDto> Sources);

/// <summary>
/// REST projection of one source declaration of a rollup (AB#5157): the source archive plus its
/// validity span (<see cref="ValidFrom"/> inclusive, <see cref="ValidTo"/> exclusive; null = unbounded
/// in that direction). Mirrors the SDK <c>RollupSourceReferenceDto</c> exactly.
/// </summary>
public sealed record RollupSourceRestDto(string SourceArchiveRtId, DateTime? ValidFrom, DateTime? ValidTo);
