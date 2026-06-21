namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Diagnostics;

/// <summary>
/// Aggregated slow-query group returned when the diagnostics endpoint is called with
/// <c>?groupBy=fingerprint</c>. One entry per structural fingerprint, summarising every
/// matching slow command in the buffer (AB#4213). The <c>Database</c> field of the
/// underlying engine group is elided here — the endpoint is tenant-scoped, so it's implied
/// by the route's <c>tenantId</c>.
/// </summary>
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
    SlowQueryEntryDto Representative);
