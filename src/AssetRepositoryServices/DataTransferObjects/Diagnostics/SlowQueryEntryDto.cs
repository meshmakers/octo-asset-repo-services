namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Diagnostics;

/// <summary>
/// One slow MongoDB command captured for the Refinery Studio Diagnostics surface (AB#4212).
/// The <c>Database</c> field of the engine-level entry is intentionally elided here — the
/// endpoint is tenant-scoped and the Database is implied by the route's <c>tenantId</c>.
/// </summary>
public sealed record SlowQueryEntryDto(
    DateTimeOffset Timestamp,
    string CommandName,
    string Target,
    double DurationMs,
    int RequestId,
    string CommandBsonPreview,
    bool Success,
    string? ErrorCode);
