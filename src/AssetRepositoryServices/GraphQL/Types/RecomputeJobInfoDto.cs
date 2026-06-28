using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
/// Projection of a <see cref="RecomputeJobSnapshot"/> for the GraphQL / REST recompute surface
/// (AB#4184). Carries the debuggable per-run fields returned by <c>recomputeArchive</c> and
/// <c>recomputeJobsFor</c>.
/// </summary>
internal sealed record RecomputeJobInfoDto(
    OctoObjectId RtId,
    RecomputeJobState State,
    int? RowsProcessed,
    int? WindowsProcessed,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    int? DurationMs,
    string? ErrorReason)
{
    /// <summary>Maps an engine snapshot to the API projection.</summary>
    public static RecomputeJobInfoDto From(RecomputeJobSnapshot job) => new(
        job.RtId, job.State, job.RowsProcessed, job.WindowsProcessed,
        job.StartedAt, job.FinishedAt, job.DurationMs, job.ErrorReason);
}
