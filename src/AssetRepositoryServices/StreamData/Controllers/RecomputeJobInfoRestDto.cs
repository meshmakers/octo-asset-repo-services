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
    string? ErrorReason)
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
        job.ErrorReason);
}
