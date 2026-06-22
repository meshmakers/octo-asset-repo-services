namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Diagnostics;

/// <summary>
/// Parsed result of running <c>db.runCommand({explain: ..., verbosity: "queryPlanner"})</c>
/// against the originating database for a captured slow query (AB#4216). Attached to
/// <see cref="SlowQueryEntryDto"/> and <see cref="SlowQueryGroupDto"/> at read time so the
/// Refinery Studio Diagnostics surface can show whether the query is a <c>COLLSCAN</c> and
/// which indexes (if any) were considered.
/// </summary>
/// <param name="CapturedAt">UTC instant when the explain probe finished (or failed).</param>
/// <param name="Status">
/// Outcome marker — <c>"success"</c>, <c>"unsupported"</c>, or <c>"failed"</c>. Exposed as a
/// string so the API contract is stable against enum renames on the engine side.
/// </param>
/// <param name="WinningStage">
/// Top-level <c>queryPlanner.winningPlan.stage</c> value (e.g. <c>COLLSCAN</c>, <c>IXSCAN</c>,
/// <c>FETCH</c>, <c>SORT</c>). Empty string when <see cref="Status"/> is not <c>success</c>.
/// </param>
/// <param name="HasCollScan">
/// <c>true</c> if any node in the winning-plan tree is a <c>COLLSCAN</c> (including nested
/// under a <c>FETCH</c> / <c>SORT</c>). The headline signal of Stage 2B.
/// </param>
/// <param name="IndexNames">
/// Every <c>IXSCAN.indexName</c> encountered, in document order. Empty for full scans.
/// </param>
/// <param name="RawExplainPreview">
/// Truncated JSON of the <c>queryPlanner</c> sub-document, capped at
/// <c>SlowQueryExplainPreviewBytes</c> UTF-8 bytes — for power users / deeper drill-down.
/// <c>null</c> on non-success outcomes.
/// </param>
/// <param name="ErrorMessage">
/// Short failure cause when <see cref="Status"/> is not <c>success</c> (e.g. <c>"timeout"</c>,
/// <c>"command type 'insert' is not explainable"</c>). <c>null</c> otherwise.
/// </param>
public sealed record SlowQueryExplainDto(
    DateTimeOffset CapturedAt,
    string Status,
    string WinningStage,
    bool HasCollScan,
    IReadOnlyList<string> IndexNames,
    string? RawExplainPreview,
    string? ErrorMessage);
