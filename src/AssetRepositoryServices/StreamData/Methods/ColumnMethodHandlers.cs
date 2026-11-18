using System.Text.Json;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Services.Methods;
using static Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Methods.ArchiveMethodSupport;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Methods;

/// <summary>
///     Steps the archive column methods share (AB#8600, System.StreamData 1.28.0).
/// </summary>
/// <remarks>
///     The queuing writes of a column change run under the host's stopping token, never under the call's token, exactly as the
///     mutations do (AB#8002, <c>StreamDataMutation.HostToken</c>): a caller that gives up mid-call cannot leave a half-queued
///     column behind. The backfill itself runs on the background drain.
/// </remarks>
internal static class ArchiveColumnChanges
{
    /// <summary>The host's stopping token (AB#8002); <see cref="CancellationToken.None" /> outside a host.</summary>
    public static CancellationToken HostToken(IHostApplicationLifetime? lifetime) =>
        lifetime?.ApplicationStopping ?? CancellationToken.None;

    /// <summary>
    ///     Runs an engine column call and turns the engine's refusal of an unknown computed column (an
    ///     <see cref="InvalidOperationException" />, "Computed column 'x' does not exist on archive ...") into a stream-data refusal,
    ///     so the method answers the declared <c>STREAMDATA_ERROR</c> with the engine message instead of the undeclared
    ///     <c>METHOD_HANDLER_FAILED</c>. The mutation answers this case with its generic error code.
    /// </summary>
    public static async Task<T> RefusingUnknownColumnsAsync<T>(OctoObjectId archiveRtId, Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (InvalidOperationException e)
        {
            throw new StreamDataException(e.Message, archiveRtId, e);
        }
    }

    /// <inheritdoc cref="RefusingUnknownColumnsAsync{T}" />
    public static Task RefusingUnknownColumnsAsync(OctoObjectId archiveRtId, Func<Task> call) =>
        RefusingUnknownColumnsAsync(archiveRtId, async () =>
        {
            await call().ConfigureAwait(false);
            return true;
        });

    /// <summary>A UTC instant as the mutation passes it on (<c>StreamDataMutation.ToUtc</c>).</summary>
    public static DateTime? ToUtc(DateTime? value) => value is { } v
        ? v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime()
        : null;

    /// <summary>
    ///     Runs a column change that answers a recompute job (retry, repair) as the mutation does: the lifecycle service of the
    ///     tenant, the change, the job as <c>RecomputeJobInfoDto.From</c> maps it (no result when no job store is wired). A
    ///     stream-data refusal is the declared error with the code of the mutation.
    /// </summary>
    public static async Task<CkMethodOutcome<JobAnswer>> RunJobAsync(ISystemContext systemContext, ILogger logger,
        CkMethodContext context, string changeName,
        Func<IArchiveLifecycleService, OctoObjectId, Task<RecomputeJobSnapshot?>> change)
    {
        if (!TryGetTarget(context, out var archiveRtId))
        {
            return CkMethodOutcome<JobAnswer>.TargetNotFound();
        }

        var tenant = await FindTenantAsync(systemContext, context).ConfigureAwait(false);
        if (tenant?.GetArchiveLifecycleService() is not { } lifecycle)
        {
            return CkMethodOutcome<JobAnswer>.Fail(ArchiveMethodErrors.StreamDataNotEnabled);
        }

        // no target id in the log (AGENTS.md: never log a method call's target id); the invocation id correlates
        logger.LogDebug("Archive {Change} requested (CK method, invocation {InvocationId})", changeName, context.InvocationId);
        try
        {
            var job = await change(lifecycle, archiveRtId).ConfigureAwait(false);
            return CkMethodOutcome<JobAnswer>.Ok(new JobAnswer(job is null ? null : RollupTransitions.ToRecord(job)));
        }
        catch (StreamDataException e)
        {
            logger.LogInformation("Archive {Change} refused (CK method, invocation {InvocationId}): {Code}", changeName,
                context.InvocationId, ArchiveMethodErrors.CodeOf(e));
            return CkMethodOutcome<JobAnswer>.Fail(ArchiveMethodErrors.FailureOf(e));
        }
    }

    /// <summary>The job of a retry or repair; <c>null</c> when no job store is wired (the mutation answers null then).</summary>
    internal sealed record JobAnswer(RtRecomputeJobInfoRecord? Job);

    /// <summary>
    ///     The CK result type of a REST <see cref="FormulaResultType" /> (AB#9057), by member name like
    ///     <see cref="ArchiveMethodSupport.TryToEngine{TEngine}" /> maps it back. A value outside the engine enum keeps its
    ///     number, so the method refuses it as <c>METHOD_VALIDATION_FAILED</c> on <c>resultType</c> instead of guessing.
    /// </summary>
    public static RtCkComputedColumnResultTypeEnum ResultTypeOf(FormulaResultType resultType) =>
        Enum.IsDefined(resultType) && Enum.TryParse<RtCkComputedColumnResultTypeEnum>(resultType.ToString(), ignoreCase: false,
            out var ckValue) && Enum.IsDefined(ckValue)
            ? ckValue
            : (RtCkComputedColumnResultTypeEnum)(int)resultType;

    /// <summary>
    ///     The job a successful retry or repair answered (the <c>RecomputeJobInfo</c> record of the result), or <c>null</c> when
    ///     the result holds none (AB#9057, the REST facade answers <c>204</c> then).
    /// </summary>
    public static RtRecomputeJobInfoRecord? JobOf(CkMethodResponse response)
    {
        if (!response.Success || string.IsNullOrEmpty(response.ResultJson))
        {
            return null;
        }

        using var document = JsonDocument.Parse(response.ResultJson);
        var root = document.RootElement;
        // the typed result wraps its value, as ArchiveMethodFacade.TransitionOf reads it; accept the bare record as well
        var record = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("value", out var value) ? value : root;
        return record.ValueKind == JsonValueKind.Object
            ? record.Deserialize<RtRecomputeJobInfoRecord>(CkMethodJson.Options)
            : null;
    }
}

/// <summary>
///     Handler of <c>System.StreamData/Archive.AddComputedColumn-1</c> (AB#8600). Mutation parity: <c>streamData.addComputedColumn</c>
///     (<see cref="IArchiveLifecycleService.EnqueueAddComputedColumnAsync" />): the column is added and its backfill queued; the
///     answer carries the archive status and the transition name <c>AddComputedColumn</c>.
/// </summary>
internal sealed class ArchiveAddComputedColumnHandler(
    ISystemContext systemContext,
    ILogger<ArchiveAddComputedColumnHandler> logger,
    IHostApplicationLifetime? lifetime = null)
    : ICkMethodHandler<ArchiveAddComputedColumnParameters, ArchiveAddComputedColumnResult>
{
    public async Task<CkMethodOutcome<ArchiveAddComputedColumnResult>> HandleAsync(
        CkMethodContext<ArchiveAddComputedColumnParameters> context, CancellationToken cancellationToken)
    {
        var parameters = context.Parameters;
        if (!TryToEngine<FormulaResultType>(parameters.ResultType, out var resultType))
        {
            return CkMethodOutcome<ArchiveAddComputedColumnResult>.ValidationFailed("resultType", "unknown-value");
        }

        var indexed = parameters.Indexed ?? true;
        var hostToken = ArchiveColumnChanges.HostToken(lifetime);
        var outcome = await ArchiveTransitions.RunAsync(systemContext, logger, context, "AddComputedColumn",
            (service, id) => ArchiveColumnChanges.RefusingUnknownColumnsAsync(id,
                () => service.EnqueueAddComputedColumnAsync(id, parameters.Name, parameters.Formula, resultType, indexed,
                    hostToken))).ConfigureAwait(false);
        return outcome.IsSuccess
            ? new ArchiveAddComputedColumnResult { Value = outcome.Value! }
            : CkMethodOutcome<ArchiveAddComputedColumnResult>.Fail(outcome.Failure!);
    }
}

/// <summary>
///     Handler of <c>System.StreamData/Archive.AddColumn-1</c> (AB#8600). Mutation parity: <c>streamData.addArchiveColumn</c>
///     (<see cref="IArchiveLifecycleService.AddArchiveColumnAsync" />), transition name <c>AddArchiveColumn</c>. A path that
///     reaches a Hidden attribute is refused by the engine (<see cref="HiddenAttributeInArchiveException" />) and answered with the
///     mutation's code <c>STREAMDATA_ERROR</c> and the engine message (AB#6247).
/// </summary>
internal sealed class ArchiveAddColumnHandler(
    ISystemContext systemContext,
    ILogger<ArchiveAddColumnHandler> logger,
    IHostApplicationLifetime? lifetime = null)
    : ICkMethodHandler<ArchiveAddColumnParameters, ArchiveAddColumnResult>
{
    public async Task<CkMethodOutcome<ArchiveAddColumnResult>> HandleAsync(CkMethodContext<ArchiveAddColumnParameters> context,
        CancellationToken cancellationToken)
    {
        var path = context.Parameters.Path;
        var indexed = context.Parameters.Indexed ?? false;
        var hostToken = ArchiveColumnChanges.HostToken(lifetime);
        var outcome = await ArchiveTransitions.RunAsync(systemContext, logger, context, "AddArchiveColumn",
            (service, id) => ArchiveColumnChanges.RefusingUnknownColumnsAsync(id,
                () => service.AddArchiveColumnAsync(id, path, indexed, hostToken))).ConfigureAwait(false);
        return outcome.IsSuccess
            ? new ArchiveAddColumnResult { Value = outcome.Value! }
            : CkMethodOutcome<ArchiveAddColumnResult>.Fail(outcome.Failure!);
    }
}

/// <summary>
///     Handler of <c>System.StreamData/Archive.RemoveComputedColumn-1</c> (AB#8600). Mutation parity:
///     <c>streamData.removeComputedColumn</c> (<see cref="IArchiveLifecycleService.RemoveComputedColumnAsync" />).
/// </summary>
internal sealed class ArchiveRemoveComputedColumnHandler(
    ISystemContext systemContext,
    ILogger<ArchiveRemoveComputedColumnHandler> logger)
    : ICkMethodHandler<ArchiveRemoveComputedColumnParameters, ArchiveRemoveComputedColumnResult>
{
    public async Task<CkMethodOutcome<ArchiveRemoveComputedColumnResult>> HandleAsync(
        CkMethodContext<ArchiveRemoveComputedColumnParameters> context, CancellationToken cancellationToken)
    {
        var name = context.Parameters.Name;
        var outcome = await ArchiveTransitions.RunAsync(systemContext, logger, context, "RemoveComputedColumn",
            (service, id) => ArchiveColumnChanges.RefusingUnknownColumnsAsync(id,
                () => service.RemoveComputedColumnAsync(id, name))).ConfigureAwait(false);
        return outcome.IsSuccess
            ? new ArchiveRemoveComputedColumnResult { Value = outcome.Value! }
            : CkMethodOutcome<ArchiveRemoveComputedColumnResult>.Fail(outcome.Failure!);
    }
}

/// <summary>
///     Handler of <c>System.StreamData/Archive.UpdateComputedColumnFormula-1</c> (AB#8600). Mutation parity:
///     <c>streamData.updateComputedColumnFormula</c> (<see cref="IArchiveLifecycleService.EnqueueUpdateComputedColumnFormulaAsync" />):
///     the backfill of the new formula is queued; readers keep the previous formula until the job swaps the column version.
/// </summary>
internal sealed class ArchiveUpdateComputedColumnFormulaHandler(
    ISystemContext systemContext,
    ILogger<ArchiveUpdateComputedColumnFormulaHandler> logger,
    IHostApplicationLifetime? lifetime = null)
    : ICkMethodHandler<ArchiveUpdateComputedColumnFormulaParameters, ArchiveUpdateComputedColumnFormulaResult>
{
    public async Task<CkMethodOutcome<ArchiveUpdateComputedColumnFormulaResult>> HandleAsync(
        CkMethodContext<ArchiveUpdateComputedColumnFormulaParameters> context, CancellationToken cancellationToken)
    {
        var name = context.Parameters.Name;
        var formula = context.Parameters.Formula;
        var hostToken = ArchiveColumnChanges.HostToken(lifetime);
        var outcome = await ArchiveTransitions.RunAsync(systemContext, logger, context, "UpdateComputedColumnFormula",
            (service, id) => ArchiveColumnChanges.RefusingUnknownColumnsAsync(id,
                () => service.EnqueueUpdateComputedColumnFormulaAsync(id, name, formula, hostToken))).ConfigureAwait(false);
        return outcome.IsSuccess
            ? new ArchiveUpdateComputedColumnFormulaResult { Value = outcome.Value! }
            : CkMethodOutcome<ArchiveUpdateComputedColumnFormulaResult>.Fail(outcome.Failure!);
    }
}

/// <summary>
///     Handler of <c>System.StreamData/Archive.RetryComputedColumnBackfill-1</c> (AB#8600). Mutation parity:
///     <c>streamData.retryComputedColumnBackfill</c> (<see cref="IArchiveLifecycleService.RetryComputedColumnBackfillAsync" />):
///     answers the queued job, or the already queued job of the column.
/// </summary>
internal sealed class ArchiveRetryComputedColumnBackfillHandler(
    ISystemContext systemContext,
    ILogger<ArchiveRetryComputedColumnBackfillHandler> logger,
    IHostApplicationLifetime? lifetime = null)
    : ICkMethodHandler<ArchiveRetryComputedColumnBackfillParameters, ArchiveRetryComputedColumnBackfillResult>
{
    public async Task<CkMethodOutcome<ArchiveRetryComputedColumnBackfillResult>> HandleAsync(
        CkMethodContext<ArchiveRetryComputedColumnBackfillParameters> context, CancellationToken cancellationToken)
    {
        var name = context.Parameters.Name;
        var hostToken = ArchiveColumnChanges.HostToken(lifetime);
        var outcome = await ArchiveColumnChanges.RunJobAsync(systemContext, logger, context, "RetryComputedColumnBackfill",
            (service, id) => ArchiveColumnChanges.RefusingUnknownColumnsAsync(id,
                () => service.RetryComputedColumnBackfillAsync(id, name, hostToken))).ConfigureAwait(false);
        return outcome.IsSuccess
            ? new ArchiveRetryComputedColumnBackfillResult { Value = outcome.Value!.Job }
            : CkMethodOutcome<ArchiveRetryComputedColumnBackfillResult>.Fail(outcome.Failure!);
    }
}

/// <summary>
///     Handler of <c>System.StreamData/Archive.RepairComputedColumn-1</c> (AB#8600). Mutation parity:
///     <c>streamData.repairComputedColumn</c> (<see cref="IArchiveLifecycleService.RepairComputedColumnAsync" />): answers the queued
///     repair job, or the already queued or running repair of the column.
/// </summary>
internal sealed class ArchiveRepairComputedColumnHandler(
    ISystemContext systemContext,
    ILogger<ArchiveRepairComputedColumnHandler> logger,
    IHostApplicationLifetime? lifetime = null)
    : ICkMethodHandler<ArchiveRepairComputedColumnParameters, ArchiveRepairComputedColumnResult>
{
    public async Task<CkMethodOutcome<ArchiveRepairComputedColumnResult>> HandleAsync(
        CkMethodContext<ArchiveRepairComputedColumnParameters> context, CancellationToken cancellationToken)
    {
        var name = context.Parameters.Name;
        var from = ArchiveColumnChanges.ToUtc(context.Parameters.From);
        var to = ArchiveColumnChanges.ToUtc(context.Parameters.To);
        var hostToken = ArchiveColumnChanges.HostToken(lifetime);
        var outcome = await ArchiveColumnChanges.RunJobAsync(systemContext, logger, context, "RepairComputedColumn",
            (service, id) => ArchiveColumnChanges.RefusingUnknownColumnsAsync(id,
                () => service.RepairComputedColumnAsync(id, name, from, to, hostToken))).ConfigureAwait(false);
        return outcome.IsSuccess
            ? new ArchiveRepairComputedColumnResult { Value = outcome.Value!.Job }
            : CkMethodOutcome<ArchiveRepairComputedColumnResult>.Fail(outcome.Failure!);
    }
}
