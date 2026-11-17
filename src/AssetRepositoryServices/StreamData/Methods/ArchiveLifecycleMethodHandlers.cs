using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Services.Methods;
using static Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Methods.ArchiveMethodSupport;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Methods;

/// <summary>
///     The transition of one archive lifecycle method (AB#8598): the <see cref="IArchiveLifecycleService" /> call of the
///     mutation it mirrors and the transition name the mutation reports.
/// </summary>
internal static class ArchiveTransitions
{
    /// <summary>
    ///     Runs a transition exactly as <c>StreamDataMutation.ResolveTransitionAsync</c> does: the lifecycle service of the
    ///     tenant, the transition, then the status read back from the archive store. A stream-data refusal is the declared error
    ///     with the code of the mutation.
    /// </summary>
    public static async Task<CkMethodOutcome<RtArchiveTransitionResultRecord>> RunAsync(ISystemContext systemContext,
        ILogger logger, CkMethodContext context, string transitionName,
        Func<IArchiveLifecycleService, OctoObjectId, Task> transition)
    {
        if (!TryGetTarget(context, out var archiveRtId))
        {
            return CkMethodOutcome<RtArchiveTransitionResultRecord>.TargetNotFound();
        }

        var tenant = await FindTenantAsync(systemContext, context).ConfigureAwait(false);
        if (tenant?.GetArchiveLifecycleService() is not { } lifecycle)
        {
            return CkMethodOutcome<RtArchiveTransitionResultRecord>.Fail(ArchiveMethodErrors.StreamDataNotEnabled);
        }

        // no target id or engine message in the log (AGENTS.md: never log a method call's target id or parameters)
        logger.LogDebug("Archive {Transition} requested (CK method, invocation {InvocationId})", transitionName,
            context.InvocationId);
        try
        {
            await transition(lifecycle, archiveRtId).ConfigureAwait(false);
            var snapshot = await tenant.GetArchiveRuntimeStore().GetAsync(archiveRtId).ConfigureAwait(false)
                           ?? throw new ArchiveNotFoundException(archiveRtId);
            return CkMethodOutcome<RtArchiveTransitionResultRecord>.Ok(new RtArchiveTransitionResultRecord
            {
                ArchiveRtId = archiveRtId.ToString(),
                Status = (RtCkArchiveStatusEnum)(int)snapshot.Status,
                Transition = transitionName
            });
        }
        catch (StreamDataException e)
        {
            logger.LogInformation("Archive {Transition} refused (CK method, invocation {InvocationId}): {Code}", transitionName,
                context.InvocationId, ArchiveMethodErrors.CodeOf(e));
            return CkMethodOutcome<RtArchiveTransitionResultRecord>.Fail(ArchiveMethodErrors.FailureOf(e));
        }
    }
}

/// <summary>
///     Handler of <c>System.StreamData/Archive.Activate-1</c> (AB#8598). Mutation parity: <c>streamData.activateArchive</c>
///     (<see cref="IArchiveLifecycleService.ActivateAsync" />).
/// </summary>
internal sealed class ArchiveActivateHandler(ISystemContext systemContext, ILogger<ArchiveActivateHandler> logger)
    : ICkMethodHandler<ArchiveActivateParameters, ArchiveActivateResult>
{
    public async Task<CkMethodOutcome<ArchiveActivateResult>> HandleAsync(CkMethodContext<ArchiveActivateParameters> context,
        CancellationToken cancellationToken)
    {
        var outcome = await ArchiveTransitions.RunAsync(systemContext, logger, context, "Activate",
            (lifecycle, id) => lifecycle.ActivateAsync(id)).ConfigureAwait(false);
        return outcome.IsSuccess
            ? new ArchiveActivateResult { Value = outcome.Value! }
            : CkMethodOutcome<ArchiveActivateResult>.Fail(outcome.Failure!);
    }
}

/// <summary>
///     Handler of <c>System.StreamData/Archive.Disable-1</c> (AB#8598). Mutation parity: <c>streamData.disableArchive</c>
///     (<see cref="IArchiveLifecycleService.DisableAsync" />).
/// </summary>
internal sealed class ArchiveDisableHandler(ISystemContext systemContext, ILogger<ArchiveDisableHandler> logger)
    : ICkMethodHandler<ArchiveDisableParameters, ArchiveDisableResult>
{
    public async Task<CkMethodOutcome<ArchiveDisableResult>> HandleAsync(CkMethodContext<ArchiveDisableParameters> context,
        CancellationToken cancellationToken)
    {
        var outcome = await ArchiveTransitions.RunAsync(systemContext, logger, context, "Disable",
            (lifecycle, id) => lifecycle.DisableAsync(id)).ConfigureAwait(false);
        return outcome.IsSuccess
            ? new ArchiveDisableResult { Value = outcome.Value! }
            : CkMethodOutcome<ArchiveDisableResult>.Fail(outcome.Failure!);
    }
}

/// <summary>
///     Handler of <c>System.StreamData/Archive.Enable-1</c> (AB#8598). Mutation parity: <c>streamData.enableArchive</c>
///     (<see cref="IArchiveLifecycleService.EnableAsync" />).
/// </summary>
internal sealed class ArchiveEnableHandler(ISystemContext systemContext, ILogger<ArchiveEnableHandler> logger)
    : ICkMethodHandler<ArchiveEnableParameters, ArchiveEnableResult>
{
    public async Task<CkMethodOutcome<ArchiveEnableResult>> HandleAsync(CkMethodContext<ArchiveEnableParameters> context,
        CancellationToken cancellationToken)
    {
        var outcome = await ArchiveTransitions.RunAsync(systemContext, logger, context, "Enable",
            (lifecycle, id) => lifecycle.EnableAsync(id)).ConfigureAwait(false);
        return outcome.IsSuccess
            ? new ArchiveEnableResult { Value = outcome.Value! }
            : CkMethodOutcome<ArchiveEnableResult>.Fail(outcome.Failure!);
    }
}

/// <summary>
///     Handler of <c>System.StreamData/Archive.RetryActivation-1</c> (AB#8598). Mutation parity:
///     <c>streamData.retryArchiveActivation</c> (<see cref="IArchiveLifecycleService.RetryActivationAsync" />).
/// </summary>
internal sealed class ArchiveRetryActivationHandler(ISystemContext systemContext, ILogger<ArchiveRetryActivationHandler> logger)
    : ICkMethodHandler<ArchiveRetryActivationParameters, ArchiveRetryActivationResult>
{
    public async Task<CkMethodOutcome<ArchiveRetryActivationResult>> HandleAsync(
        CkMethodContext<ArchiveRetryActivationParameters> context, CancellationToken cancellationToken)
    {
        var outcome = await ArchiveTransitions.RunAsync(systemContext, logger, context, "RetryActivation",
            (lifecycle, id) => lifecycle.RetryActivationAsync(id)).ConfigureAwait(false);
        return outcome.IsSuccess
            ? new ArchiveRetryActivationResult { Value = outcome.Value! }
            : CkMethodOutcome<ArchiveRetryActivationResult>.Fail(outcome.Failure!);
    }
}

/// <summary>
///     Handler of <c>System.StreamData/Archive.DeleteArchive-1</c> (AB#8598). Mutation parity: <c>streamData.deleteArchive</c>
///     (<see cref="IArchiveLifecycleService.DeleteAsync" />): drops the table (idempotent) and deletes the entity; the result is
///     <c>true</c>, as the mutation answers.
/// </summary>
internal sealed class ArchiveDeleteArchiveHandler(ISystemContext systemContext, ILogger<ArchiveDeleteArchiveHandler> logger)
    : ICkMethodHandler<ArchiveDeleteArchiveParameters, ArchiveDeleteArchiveResult>
{
    public async Task<CkMethodOutcome<ArchiveDeleteArchiveResult>> HandleAsync(
        CkMethodContext<ArchiveDeleteArchiveParameters> context, CancellationToken cancellationToken)
    {
        if (!TryGetTarget(context, out var archiveRtId))
        {
            return CkMethodOutcome<ArchiveDeleteArchiveResult>.TargetNotFound();
        }

        var tenant = await FindTenantAsync(systemContext, context).ConfigureAwait(false);
        if (tenant?.GetArchiveLifecycleService() is not { } lifecycle)
        {
            return CkMethodOutcome<ArchiveDeleteArchiveResult>.Fail(ArchiveMethodErrors.StreamDataNotEnabled);
        }

        logger.LogDebug("Archive Delete requested for {ArchiveRtId} (CK method, invocation {InvocationId})", archiveRtId,
            context.InvocationId);
        try
        {
            await lifecycle.DeleteAsync(archiveRtId).ConfigureAwait(false);
            return new ArchiveDeleteArchiveResult { Value = true };
        }
        catch (StreamDataException e)
        {
            logger.LogInformation("Archive Delete of {ArchiveRtId} refused (CK method): {Reason}", archiveRtId, e.Message);
            return CkMethodOutcome<ArchiveDeleteArchiveResult>.Fail(ArchiveMethodErrors.FailureOf(e));
        }
    }
}

/// <summary>
///     Handler of <c>System.StreamData/Archive.RedactHiddenColumns-1</c> (System.StreamData 1.27.0, deprecated since 1.29.0;
///     <see cref="IArchiveLifecycleService.RedactHiddenColumnsAsync" />). <b>Destructive</b>: drops the archive columns that reach
///     a Hidden attribute at execution time and their stored values, then resumes an archive that was Failed on them. Refused
///     with <c>STREAMDATA_CONFIRMATION_REQUIRED</c> unless <c>confirmDataLoss</c> is true; the role (<c>StreamDataAdmin</c>) is
///     enforced by the invocation pipeline from the model. No streamData mutation exists for it. Interactive callers use
///     <see cref="ArchiveRedactHiddenColumns2Handler" />.
/// </summary>
internal sealed class ArchiveRedactHiddenColumnsHandler(
    ISystemContext systemContext,
    ILogger<ArchiveRedactHiddenColumnsHandler> logger)
    : ICkMethodHandler<ArchiveRedactHiddenColumnsParameters, ArchiveRedactHiddenColumnsResult>
{
    /// <summary>The error codes <c>Archive.RedactHiddenColumns-1</c> declares in the model.</summary>
    internal static readonly IReadOnlySet<string> RedactDeclaredCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        Statics.GraphQlErrorStreamDataArchiveNotFound, ArchiveMethodErrors.ConfirmationRequired,
        ArchiveMethodErrors.RedactionBlocked, Statics.GraphQlErrorStreamDataActivationFailed, Statics.GraphQlErrorStreamData,
        ArchiveMethodErrors.StreamDataNotEnabled
    };

    public Task<CkMethodOutcome<ArchiveRedactHiddenColumnsResult>> HandleAsync(
        CkMethodContext<ArchiveRedactHiddenColumnsParameters> context, CancellationToken cancellationToken) =>
        ArchiveRedaction.RunAsync(systemContext, logger, context, "RedactHiddenColumns", context.Parameters.ConfirmDataLoss,
            expectedColumns: null, RedactDeclaredCodes,
            (lifecycle, archiveRtId) => lifecycle.RedactHiddenColumnsAsync(archiveRtId),
            record => new ArchiveRedactHiddenColumnsResult { Value = record });
}

/// <summary>
///     Handler of <c>System.StreamData/Archive.RedactHiddenColumns-2</c> (System.StreamData 1.29.0;
///     <see cref="IArchiveLifecycleService.RedactConfirmedHiddenColumnsAsync" />). <b>Destructive</b>, as -1, but it drops only
///     when the columns that reach a Hidden attribute at execution time are exactly the required <c>expectedColumns</c> (ignoring
///     case, order and duplicates); otherwise it answers <c>STREAMDATA_REDACTION_COLUMNS_CHANGED</c> with the parameters
///     <c>expectedColumns</c> and <c>currentColumns</c> and nothing is dropped. Same role, confirmation and result as -1.
/// </summary>
internal sealed class ArchiveRedactHiddenColumns2Handler(
    ISystemContext systemContext,
    ILogger<ArchiveRedactHiddenColumns2Handler> logger)
    : ICkMethodHandler<ArchiveRedactHiddenColumns2Parameters, ArchiveRedactHiddenColumns2Result>
{
    /// <summary>The error codes <c>Archive.RedactHiddenColumns-2</c> declares in the model.</summary>
    internal static readonly IReadOnlySet<string> Redact2DeclaredCodes =
        new HashSet<string>(ArchiveRedactHiddenColumnsHandler.RedactDeclaredCodes, StringComparer.Ordinal)
        {
            ArchiveMethodErrors.RedactionColumnsChanged
        };

    public Task<CkMethodOutcome<ArchiveRedactHiddenColumns2Result>> HandleAsync(
        CkMethodContext<ArchiveRedactHiddenColumns2Parameters> context, CancellationToken cancellationToken)
    {
        // Required in the model; the invocation pipeline refuses a call without it. An absent list is never read as "drop
        // whatever is Hidden": it confirms no column.
        IReadOnlyCollection<string> expected = context.Parameters.ExpectedColumns ?? [];
        return ArchiveRedaction.RunAsync(systemContext, logger, context, "RedactHiddenColumns-2",
            context.Parameters.ConfirmDataLoss, expected, Redact2DeclaredCodes,
            (lifecycle, archiveRtId) => lifecycle.RedactConfirmedHiddenColumnsAsync(archiveRtId, expected),
            record => new ArchiveRedactHiddenColumns2Result { Value = record });
    }
}

/// <summary>The steps both redaction methods share: target, confirmation, tenant, the engine call and its errors.</summary>
internal static class ArchiveRedaction
{
    public static async Task<CkMethodOutcome<TResult>> RunAsync<TParameters, TResult>(
        ISystemContext systemContext,
        ILogger logger,
        CkMethodContext<TParameters> context,
        string methodName,
        bool confirmDataLoss,
        IReadOnlyCollection<string>? expectedColumns,
        IReadOnlySet<string> declaredCodes,
        Func<IArchiveLifecycleService, OctoObjectId, Task<ArchiveRedactionResult>> redact,
        Func<RtArchiveRedactionResultRecord, TResult> wrap)
        where TParameters : class
        where TResult : class
    {
        if (!TryGetTarget(context, out var archiveRtId))
        {
            return CkMethodOutcome<TResult>.TargetNotFound();
        }

        if (!confirmDataLoss)
        {
            return CkMethodOutcome<TResult>.Fail(new CkMethodFailure(
                ArchiveMethodErrors.ConfirmationRequired,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["archiveRtId"] = archiveRtId.ToString(),
                    [ArchiveMethodErrors.MessageParameter] =
                        $"{methodName} drops columns and their stored values; call it with confirmDataLoss = true."
                }));
        }

        var tenant = await FindTenantAsync(systemContext, context).ConfigureAwait(false);
        if (tenant?.GetArchiveLifecycleService() is not { } lifecycle)
        {
            return CkMethodOutcome<TResult>.Fail(ArchiveMethodErrors.StreamDataNotEnabled);
        }

        try
        {
            var result = await redact(lifecycle, archiveRtId).ConfigureAwait(false);
            // The method call logs no target id (method gateway rule); the engine's lifecycle log and audit trail record
            // the archive and the dropped columns, as for every archive operation.
            logger.LogWarning("{Method} dropped {Count} column(s) (CK method, invocation {InvocationId}), status {Status}",
                methodName, result.RemovedColumns.Count, context.InvocationId, result.Status);
            return wrap(new RtArchiveRedactionResultRecord
            {
                ArchiveRtId = archiveRtId.ToString(),
                Status = (RtCkArchiveStatusEnum)(int)result.Status,
                RemovedColumns = new AttributeStringValueList(result.RemovedColumns.ToList())
            });
        }
        catch (StreamDataException e)
        {
            var failure = ArchiveMethodErrors.FailureOf(e);
            logger.LogInformation("{Method} refused (CK method, invocation {InvocationId}, {ExpectedCount} confirmed column(s)): {Code}",
                methodName, context.InvocationId, expectedColumns?.Count, failure.Code);
            // The methods declare fewer codes than the mutations answer (no mutation exists for them); an undeclared code
            // would be replaced by METHOD_HANDLER_FAILED and lose the reason, so it becomes the declared STREAMDATA_ERROR.
            return CkMethodOutcome<TResult>.Fail(
                declaredCodes.Contains(failure.Code)
                    ? failure
                    : new CkMethodFailure(Statics.GraphQlErrorStreamData, failure.Parameters));
        }
    }
}
