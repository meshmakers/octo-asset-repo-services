using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Controllers;

/// <summary>
/// REST projection of a recompute job snapshot (AB#4184), returned by the recompute POST endpoint
/// and the recompute-jobs GET endpoint. Mirrors the SDK <c>RollupRecomputeJobInfoDto</c> exactly.
/// </summary>
public sealed record RecomputeJobInfoRestDto(
    string RtId,
    string State,
    int? RowsProcessed,
    int? WindowsProcessed,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    int? DurationMs,
    string? ErrorReason,
    DateTime? LastProgressAt,
    string? ComputedColumnName = null)
{
    /// <summary>Maps an engine snapshot to the REST projection.</summary>
    public static RecomputeJobInfoRestDto From(RecomputeJobSnapshot job) => new(
        job.RtId.ToString(),
        job.State.ToString(),
        job.RowsProcessed,
        job.WindowsProcessed,
        job.StartedAt,
        job.FinishedAt,
        job.DurationMs,
        job.ErrorReason,
        job.LastProgressAt,
        job.ComputedColumnName);

    /// <summary>
    ///     Maps the <c>RecomputeJobInfo</c> record a column method answers (<c>Archive.RetryComputedColumnBackfill-1</c>,
    ///     <c>Archive.RepairComputedColumn-1</c>) to the REST projection: the same fields as <see cref="From(RecomputeJobSnapshot)" />,
    ///     because the record is built from that snapshot.
    /// </summary>
    public static RecomputeJobInfoRestDto From(RtRecomputeJobInfoRecord job) => new(
        job.JobRtId ?? string.Empty,
        job.State.ToString(),
        job.RowsProcessed,
        job.WindowsProcessed,
        job.StartedAt,
        job.FinishedAt,
        job.DurationMs,
        job.ErrorReason,
        job.LastProgressAt,
        job.ComputedColumnName);
}
