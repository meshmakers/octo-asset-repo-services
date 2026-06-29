using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
/// GraphQL projection of <see cref="RecomputeJobInfoDto"/>: one recompute run's state, counts,
/// timings, and failure reason. Returned by the <c>recomputeArchive</c> mutation and the
/// <c>recomputeJobsFor</c> query (AB#4184).
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class RecomputeJobInfoDtoType : ObjectGraphType<RecomputeJobInfoDto>
{
    public RecomputeJobInfoDtoType()
    {
        Name = "RecomputeJobInfo";
        Description = "A rollup recompute run: state, row/window counts, timings, and failure reason.";

        Field<NonNullGraphType<OctoObjectIdType>>("rtId")
            .Description("Runtime id of the recompute job.")
            .Resolve(ctx => ctx.Source!.RtId);

        Field<NonNullGraphType<StringGraphType>>("state")
            .Description("Job lifecycle state: Pending / Running / Swapping / Completed / Failed / Coalesced.")
            .Resolve(ctx => ctx.Source!.State.ToString());

        Field<IntGraphType>("rowsProcessed")
            .Description("Rows written into the staging table; null while pending.")
            .Resolve(ctx => ctx.Source!.RowsProcessed);

        Field<IntGraphType>("windowsProcessed")
            .Description("Buckets recomputed; null while pending.")
            .Resolve(ctx => ctx.Source!.WindowsProcessed);

        Field<DateTimeGraphType>("startedAt")
            .Description("When compute started; null while pending.")
            .Resolve(ctx => ctx.Source!.StartedAt);

        Field<DateTimeGraphType>("finishedAt")
            .Description("When the job reached a terminal state; null while running.")
            .Resolve(ctx => ctx.Source!.FinishedAt);

        Field<IntGraphType>("durationMs")
            .Description("Wall-clock duration in milliseconds; null while running.")
            .Resolve(ctx => ctx.Source!.DurationMs);

        Field<StringGraphType>("errorReason")
            .Description("Failure reason when state is Failed; null otherwise.")
            .Resolve(ctx => ctx.Source!.ErrorReason);
    }
}
