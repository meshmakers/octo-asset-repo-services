using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Controllers;

/// <summary>
///     One thing the rollup validator found wrong with a rollup (AB#6968). The SDK declares the same wire shape
///     (<c>RollupConsistencyFindingDto</c>); check names travel as strings so a new check does not break an older client.
/// </summary>
public sealed record RollupConsistencyFindingRestDto(
    string Check,
    string Message,
    DateTime? RangeFromUtc,
    DateTime? RangeToUtc,
    long? Generation,
    long? Rows,
    string? Gap)
{
    internal static RollupConsistencyFindingRestDto From(RollupConsistencyFinding finding) => new(
        finding.Check.ToString(), finding.Message, finding.RangeFromUtc, finding.RangeToUtc, finding.Generation,
        finding.Rows, finding.Gap?.ToString("c"));
}

/// <summary>The validator's result for one rollup.</summary>
public sealed record RollupConsistencyEntryRestDto(
    string RollupRtId,
    bool IsOk,
    IReadOnlyList<RollupConsistencyFindingRestDto> Findings)
{
    internal static RollupConsistencyEntryRestDto From(RollupConsistencyEntry entry) => new(
        entry.RollupRtId.ToString(), entry.IsOk, entry.Findings.Select(RollupConsistencyFindingRestDto.From).ToList());
}

/// <summary>The validator's result for a tenant: one entry per rollup that was checked.</summary>
public sealed record RollupConsistencyReportRestDto(
    bool IsOk,
    DateTime CheckedAtUtc,
    IReadOnlyList<RollupConsistencyEntryRestDto> Entries)
{
    internal static RollupConsistencyReportRestDto From(RollupConsistencyReport report) => new(
        report.IsOk, report.GeneratedAtUtc, report.Rollups.Select(RollupConsistencyEntryRestDto.From).ToList());
}

/// <summary>One step of a rollup repair.</summary>
public sealed record RollupRepairActionRestDto(
    string RollupRtId,
    string Kind,
    string Description,
    string Status,
    DateTime? RangeFromUtc,
    DateTime? RangeToUtc,
    long? Generation,
    string? Error)
{
    internal static RollupRepairActionRestDto From(RollupRepairAction action) => new(
        action.RollupRtId.ToString(), action.Kind.ToString(), action.Description, action.Status.ToString(),
        action.RangeFromUtc, action.RangeToUtc, action.Generation, action.Error);
}

/// <summary>What a rollup repair did or, on a dry run, would do.</summary>
public sealed record RollupRepairReportRestDto(
    bool DryRun,
    bool IsOk,
    RollupConsistencyReportRestDto Before,
    IReadOnlyList<RollupRepairActionRestDto> Actions,
    RollupConsistencyReportRestDto? After,
    string? FailedStep)
{
    internal static RollupRepairReportRestDto From(RollupRepairReport report) => new(
        report.DryRun, report.IsOk, RollupConsistencyReportRestDto.From(report.Before),
        report.Actions.Select(RollupRepairActionRestDto.From).ToList(),
        report.After is null ? null : RollupConsistencyReportRestDto.From(report.After), report.FailedStep);
}

/// <summary>
///     An applied rollup repair (AB#6968): a request the host executes in the background, so a large recompute is never
///     bound to an HTTP request. <c>State</c> is <c>Pending</c>, <c>Running</c>, <c>Done</c> or <c>Failed</c>.
/// </summary>
public sealed record RollupRepairRequestRestDto(
    string RequestId,
    string State,
    DateTime RequestedAtUtc,
    DateTime? FinishedAtUtc,
    IReadOnlyList<RollupRepairActionRestDto> PlannedActions,
    RollupRepairReportRestDto? Report)
{
    internal static RollupRepairRequestRestDto From(RollupRepairRequest request) => new(
        request.RequestId, request.State.ToString(), request.RequestedAtUtc, request.FinishedAtUtc,
        request.PlannedActions.Select(RollupRepairActionRestDto.From).ToList(),
        request.Report is null ? null : RollupRepairReportRestDto.From(request.Report));
}
