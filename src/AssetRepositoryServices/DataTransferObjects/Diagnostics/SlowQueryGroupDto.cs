namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Diagnostics;

/// <summary>
/// Aggregated slow-query group returned when the diagnostics endpoint is called with
/// <c>?groupBy=fingerprint</c>. One entry per structural fingerprint, summarising every
/// matching slow command in the buffer (AB#4213). The <c>Database</c> field of the
/// underlying engine group is elided here — the endpoint is tenant-scoped, so it's implied
/// by the route's <c>tenantId</c>.
/// </summary>
/// <param name="Fingerprint">16-char hex fingerprint shared by all entries in this group.</param>
/// <param name="CommandName">Driver-level command name (e.g. <c>find</c>, <c>aggregate</c>).</param>
/// <param name="Target">First BSON element value of the command — typically the target collection.</param>
/// <param name="Count">Number of matching slow commands in the current buffer snapshot.</param>
/// <param name="FirstSeen">UTC timestamp of the earliest entry in the group.</param>
/// <param name="LastSeen">UTC timestamp of the most recent entry in the group.</param>
/// <param name="MinDurationMs">Fastest observed duration across the group.</param>
/// <param name="MaxDurationMs">Slowest observed duration across the group.</param>
/// <param name="AvgDurationMs">Arithmetic mean duration across the group.</param>
/// <param name="Representative">
/// Most-recent matching slow query — carries the truncated BSON preview and its own
/// <c>Explain</c> (mirrored on the group itself, but the representative remains the canonical
/// source for any other per-entry metadata).
/// </param>
/// <param name="Explain">
/// Latest async <c>explain()</c> result for this group's fingerprint key (AB#4216). Identical
/// to <c>Representative.Explain</c>; surfaced here directly so the Studio template can render
/// the COLLSCAN badge off the group row without dereferencing.
/// </param>
public sealed record SlowQueryGroupDto(
    string Fingerprint,
    string CommandName,
    string Target,
    int Count,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    double MinDurationMs,
    double MaxDurationMs,
    double AvgDurationMs,
    SlowQueryEntryDto Representative,
    SlowQueryExplainDto? Explain = null);
