using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Services.Methods;
using static Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Methods.ArchiveMethodSupport;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Methods;

/// <summary>
///     The rollup control of one method (AB#8599): the <see cref="IRollupArchiveLifecycleService" /> call of the mutation it
///     mirrors and the transition name the mutation reports (<c>StreamDataMutation.ResolveRollupAsync</c>).
/// </summary>
internal static class RollupTransitions
{
    /// <summary>
    ///     Runs a rollup change exactly as the mutation does: the rollup lifecycle service of the tenant, the change, then the
    ///     status read back from the rollup store. A stream-data refusal is the declared error with the code of the mutation.
    /// </summary>
    public static async Task<CkMethodOutcome<RtArchiveTransitionResultRecord>> RunAsync(ISystemContext systemContext,
        ILogger logger, CkMethodContext context, string transitionName,
        Func<IRollupArchiveLifecycleService, OctoObjectId, Task> change)
    {
        if (!TryGetTarget(context, out var rollupRtId))
        {
            return CkMethodOutcome<RtArchiveTransitionResultRecord>.TargetNotFound();
        }

        var tenant = await FindTenantAsync(systemContext, context).ConfigureAwait(false);
        if (tenant?.GetRollupArchiveLifecycleService() is not { } lifecycle ||
            tenant.GetRollupArchiveRuntimeStore() is not { } store)
        {
            return CkMethodOutcome<RtArchiveTransitionResultRecord>.Fail(ArchiveMethodErrors.StreamDataNotEnabled);
        }

        logger.LogDebug("Rollup {Transition} requested for {RollupRtId} (CK method, invocation {InvocationId})",
            transitionName, rollupRtId, context.InvocationId);
        try
        {
            await change(lifecycle, rollupRtId).ConfigureAwait(false);
            var snapshot = await store.GetAsync(rollupRtId).ConfigureAwait(false)
                           ?? throw new ArchiveNotFoundException(rollupRtId);
            return CkMethodOutcome<RtArchiveTransitionResultRecord>.Ok(new RtArchiveTransitionResultRecord
            {
                ArchiveRtId = rollupRtId.ToString(),
                Status = (RtCkArchiveStatusEnum)(int)snapshot.Status,
                Transition = transitionName
            });
        }
        catch (StreamDataException e)
        {
            logger.LogInformation("Rollup {Transition} of {RollupRtId} refused (CK method): {Reason}", transitionName,
                rollupRtId, e.Message);
            return CkMethodOutcome<RtArchiveTransitionResultRecord>.Fail(ArchiveMethodErrors.FailureOf(e));
        }
    }

    /// <summary>The job as the mutations answer it (<c>RecomputeJobInfoDto.From</c>), as the CK record.</summary>
    public static RtRecomputeJobInfoRecord ToRecord(RecomputeJobSnapshot job) => new()
    {
        JobRtId = job.RtId.ToString(),
        State = Enum.Parse<RtCkRecomputeJobStateEnum>(job.State.ToString()),
        RowsProcessed = job.RowsProcessed,
        WindowsProcessed = job.WindowsProcessed,
        StartedAt = job.StartedAt,
        FinishedAt = job.FinishedAt,
        DurationMs = job.DurationMs,
        ErrorReason = job.ErrorReason,
        LastProgressAt = job.LastProgressAt
    };
}

/// <summary>
///     Handler of <c>System.StreamData/RollupArchive.Freeze-1</c> (AB#8599). Mutation parity: <c>streamData.freezeRollupArchive</c>
///     (<see cref="IRollupArchiveLifecycleService.FreezeAsync" />).
/// </summary>
internal sealed class RollupArchiveFreezeHandler(ISystemContext systemContext, ILogger<RollupArchiveFreezeHandler> logger)
    : ICkMethodHandler<RollupArchiveFreezeParameters, RollupArchiveFreezeResult>
{
    public async Task<CkMethodOutcome<RollupArchiveFreezeResult>> HandleAsync(
        CkMethodContext<RollupArchiveFreezeParameters> context, CancellationToken cancellationToken)
    {
        var until = context.Parameters.Until;
        var outcome = await RollupTransitions.RunAsync(systemContext, logger, context, "Freeze",
            (lifecycle, id) => lifecycle.FreezeAsync(id, until)).ConfigureAwait(false);
        return outcome.IsSuccess
            ? new RollupArchiveFreezeResult { Value = outcome.Value! }
            : CkMethodOutcome<RollupArchiveFreezeResult>.Fail(outcome.Failure!);
    }
}

/// <summary>
///     Handler of <c>System.StreamData/RollupArchive.Unfreeze-1</c> (AB#8599). Mutation parity:
///     <c>streamData.unfreezeRollupArchive</c> (<see cref="IRollupArchiveLifecycleService.UnfreezeAsync" />).
/// </summary>
internal sealed class RollupArchiveUnfreezeHandler(ISystemContext systemContext, ILogger<RollupArchiveUnfreezeHandler> logger)
    : ICkMethodHandler<RollupArchiveUnfreezeParameters, RollupArchiveUnfreezeResult>
{
    public async Task<CkMethodOutcome<RollupArchiveUnfreezeResult>> HandleAsync(
        CkMethodContext<RollupArchiveUnfreezeParameters> context, CancellationToken cancellationToken)
    {
        var acceptGaps = context.Parameters.AcceptGaps ?? false;
        var outcome = await RollupTransitions.RunAsync(systemContext, logger, context, "Unfreeze",
            (lifecycle, id) => lifecycle.UnfreezeAsync(id, acceptGaps)).ConfigureAwait(false);
        return outcome.IsSuccess
            ? new RollupArchiveUnfreezeResult { Value = outcome.Value! }
            : CkMethodOutcome<RollupArchiveUnfreezeResult>.Fail(outcome.Failure!);
    }
}

/// <summary>
///     Handler of <c>System.StreamData/RollupArchive.RewindWatermark-1</c> (AB#8599). Mutation parity:
///     <c>streamData.rewindRollupWatermark</c> (<see cref="IRollupArchiveLifecycleService.RewindWatermarkAsync" />).
/// </summary>
internal sealed class RollupArchiveRewindWatermarkHandler(
    ISystemContext systemContext,
    ILogger<RollupArchiveRewindWatermarkHandler> logger)
    : ICkMethodHandler<RollupArchiveRewindWatermarkParameters, RollupArchiveRewindWatermarkResult>
{
    public async Task<CkMethodOutcome<RollupArchiveRewindWatermarkResult>> HandleAsync(
        CkMethodContext<RollupArchiveRewindWatermarkParameters> context, CancellationToken cancellationToken)
    {
        var toBucketEnd = context.Parameters.ToBucketEnd;
        var outcome = await RollupTransitions.RunAsync(systemContext, logger, context, "RewindWatermark",
            (lifecycle, id) => lifecycle.RewindWatermarkAsync(id, toBucketEnd)).ConfigureAwait(false);
        return outcome.IsSuccess
            ? new RollupArchiveRewindWatermarkResult { Value = outcome.Value! }
            : CkMethodOutcome<RollupArchiveRewindWatermarkResult>.Fail(outcome.Failure!);
    }
}

/// <summary>
///     Handler of <c>System.StreamData/RollupArchive.Recompute-1</c> (AB#8599). Mutation parity: <c>streamData.recomputeArchive</c>
///     (<see cref="IRecomputeOrchestrator.RecomputeArchiveAsync" />, trigger Manual): the recompute into a staging generation and
///     the atomic generation-pointer swap of AB#4184; a refused precondition is a job in state Failed, as with the mutation.
///     Synchronous like the mutation; an async method is a later decision (F3.6).
/// </summary>
internal sealed class RollupArchiveRecomputeHandler(ISystemContext systemContext, ILogger<RollupArchiveRecomputeHandler> logger)
    : ICkMethodHandler<RollupArchiveRecomputeParameters, RollupArchiveRecomputeResult>
{
    public async Task<CkMethodOutcome<RollupArchiveRecomputeResult>> HandleAsync(
        CkMethodContext<RollupArchiveRecomputeParameters> context, CancellationToken cancellationToken)
    {
        if (!TryGetTarget(context, out var rollupRtId))
        {
            return CkMethodOutcome<RollupArchiveRecomputeResult>.TargetNotFound();
        }

        OctoObjectId? scope = null;
        if (!string.IsNullOrWhiteSpace(context.Parameters.RtIdScope))
        {
            if (!OctoObjectId.TryParse(context.Parameters.RtIdScope, out var parsed))
            {
                return CkMethodOutcome<RollupArchiveRecomputeResult>.ValidationFailed("rtIdScope", "invalid-object-id");
            }

            scope = parsed;
        }

        var tenant = await FindTenantAsync(systemContext, context).ConfigureAwait(false);
        if (tenant?.GetRecomputeOrchestrator() is not { } orchestrator)
        {
            return CkMethodOutcome<RollupArchiveRecomputeResult>.Fail(ArchiveMethodErrors.StreamDataNotEnabled);
        }

        logger.LogDebug("Recompute of {RollupRtId} [{From:O}, {To:O}) requested (CK method, invocation {InvocationId})",
            rollupRtId, context.Parameters.From, context.Parameters.To, context.InvocationId);
        try
        {
            var job = await orchestrator.RecomputeArchiveAsync(rollupRtId, context.Parameters.From, context.Parameters.To, scope,
                RecomputeTrigger.Manual, cancellationToken).ConfigureAwait(false);
            return new RollupArchiveRecomputeResult { Value = RollupTransitions.ToRecord(job) };
        }
        catch (StreamDataException e)
        {
            logger.LogInformation("Recompute of {RollupRtId} refused (CK method): {Reason}", rollupRtId, e.Message);
            return CkMethodOutcome<RollupArchiveRecomputeResult>.Fail(ArchiveMethodErrors.FailureOf(e));
        }
    }
}

/// <summary>
///     Handler of <c>System.StreamData/RollupArchive.BackfillFromSource-1</c> (AB#8599). Mutation parity:
///     <c>streamData.backfillRollupFromSource</c> (<see cref="IRecomputeOrchestrator.EnqueueBackfillFromSourceAsync" />): a durable
///     backfill over the whole source history is queued and the Pending job answered at once; no result for an empty source.
/// </summary>
internal sealed class RollupArchiveBackfillFromSourceHandler(
    ISystemContext systemContext,
    ILogger<RollupArchiveBackfillFromSourceHandler> logger)
    : ICkMethodHandler<RollupArchiveBackfillFromSourceParameters, RollupArchiveBackfillFromSourceResult>
{
    public async Task<CkMethodOutcome<RollupArchiveBackfillFromSourceResult>> HandleAsync(
        CkMethodContext<RollupArchiveBackfillFromSourceParameters> context, CancellationToken cancellationToken)
    {
        if (!TryGetTarget(context, out var rollupRtId))
        {
            return CkMethodOutcome<RollupArchiveBackfillFromSourceResult>.TargetNotFound();
        }

        var tenant = await FindTenantAsync(systemContext, context).ConfigureAwait(false);
        if (tenant?.GetRecomputeOrchestrator() is not { } orchestrator)
        {
            return CkMethodOutcome<RollupArchiveBackfillFromSourceResult>.Fail(ArchiveMethodErrors.StreamDataNotEnabled);
        }

        logger.LogDebug("Backfill-from-source of {RollupRtId} requested (CK method, invocation {InvocationId})", rollupRtId,
            context.InvocationId);
        try
        {
            // Only the source-min resolve and the enqueue run under the call; the recompute runs on the background
            // orchestrator (AB#4286), as with the mutation.
            var job = await orchestrator.EnqueueBackfillFromSourceAsync(rollupRtId, cancellationToken).ConfigureAwait(false);
            return new RollupArchiveBackfillFromSourceResult { Value = job is null ? null : RollupTransitions.ToRecord(job) };
        }
        catch (StreamDataException e)
        {
            logger.LogInformation("Backfill of {RollupRtId} refused (CK method): {Reason}", rollupRtId, e.Message);
            return CkMethodOutcome<RollupArchiveBackfillFromSourceResult>.Fail(ArchiveMethodErrors.FailureOf(e));
        }
    }
}
