namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Diagnostics;

/// <summary>
/// One slow MongoDB command captured for the Refinery Studio Diagnostics surface (AB#4212).
/// The <c>Database</c> field of the engine-level entry is intentionally elided here — the
/// endpoint is tenant-scoped and the Database is implied by the route's <c>tenantId</c>.
/// </summary>
/// <param name="Timestamp">UTC instant when the command's succeeded / failed event fired.</param>
/// <param name="CommandName">Driver-level command name (e.g. <c>find</c>, <c>aggregate</c>).</param>
/// <param name="Target">First BSON element value of the command — typically the target collection.</param>
/// <param name="DurationMs">Driver-reported duration in milliseconds.</param>
/// <param name="RequestId">MongoDB driver request id correlating started / succeeded / failed events.</param>
/// <param name="CommandBsonPreview">Truncated BSON command body in JSON form.</param>
/// <param name="Success"><c>true</c> if the command completed successfully, <c>false</c> if it failed.</param>
/// <param name="ErrorCode">Mongo error code on failure (e.g. <c>112</c> for WriteConflict). <c>null</c> on success.</param>
/// <param name="Fingerprint">Structural fingerprint of the command (16-char hex) — see AB#4213.</param>
/// <param name="Explain">
/// Latest async <c>explain()</c> result for this entry's fingerprint key (AB#4216). <c>null</c>
/// until a capture has finished — the listener never blocks on explain, so the first sighting
/// of a slow shape lands here without one and gets enriched on the next read after the probe
/// resolves.
/// </param>
public sealed record SlowQueryEntryDto(
    DateTimeOffset Timestamp,
    string CommandName,
    string Target,
    double DurationMs,
    int RequestId,
    string CommandBsonPreview,
    bool Success,
    string? ErrorCode,
    string Fingerprint,
    SlowQueryExplainDto? Explain = null);
