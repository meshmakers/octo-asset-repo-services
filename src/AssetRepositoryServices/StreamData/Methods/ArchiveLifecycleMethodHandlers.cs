using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
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
        ArchiveMethodCallerTenantGuard tenantGuard, ILogger logger, CkMethodContext context, string transitionName,
        Func<IArchiveLifecycleService, OctoObjectId, Task> transition)
    {
        if (!tenantGuard.IsAllowed(context))
        {
            return CkMethodOutcome<RtArchiveTransitionResultRecord>.Forbidden(ArchiveMethodCallerTenantGuard.Reason);
        }

        if (!TryGetTarget(context, out var archiveRtId))
        {
            return CkMethodOutcome<RtArchiveTransitionResultRecord>.TargetNotFound();
        }

        var tenant = await FindTenantAsync(systemContext, context).ConfigureAwait(false);
        if (tenant?.GetArchiveLifecycleService() is not { } lifecycle)
        {
            return CkMethodOutcome<RtArchiveTransitionResultRecord>.Fail(ArchiveMethodErrors.StreamDataNotEnabled);
        }

        logger.LogDebug("Archive {Transition} requested for {ArchiveRtId} (CK method, invocation {InvocationId})",
            transitionName, archiveRtId, context.InvocationId);
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
            logger.LogInformation("Archive {Transition} of {ArchiveRtId} refused (CK method): {Reason}", transitionName,
                archiveRtId, e.Message);
            return CkMethodOutcome<RtArchiveTransitionResultRecord>.Fail(ArchiveMethodErrors.FailureOf(e));
        }
    }
}

/// <summary>
///     Handler of <c>System.StreamData/Archive.Activate-1</c> (AB#8598). Mutation parity: <c>streamData.activateArchive</c>
///     (<see cref="IArchiveLifecycleService.ActivateAsync" />).
/// </summary>
internal sealed class ArchiveActivateHandler(ISystemContext systemContext, ArchiveMethodCallerTenantGuard tenantGuard,
    ILogger<ArchiveActivateHandler> logger)
    : ICkMethodHandler<ArchiveActivateParameters, ArchiveActivateResult>
{
    public async Task<CkMethodOutcome<ArchiveActivateResult>> HandleAsync(CkMethodContext<ArchiveActivateParameters> context,
        CancellationToken cancellationToken)
    {
        var outcome = await ArchiveTransitions.RunAsync(systemContext, tenantGuard, logger, context, "Activate",
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
internal sealed class ArchiveDisableHandler(ISystemContext systemContext, ArchiveMethodCallerTenantGuard tenantGuard,
    ILogger<ArchiveDisableHandler> logger)
    : ICkMethodHandler<ArchiveDisableParameters, ArchiveDisableResult>
{
    public async Task<CkMethodOutcome<ArchiveDisableResult>> HandleAsync(CkMethodContext<ArchiveDisableParameters> context,
        CancellationToken cancellationToken)
    {
        var outcome = await ArchiveTransitions.RunAsync(systemContext, tenantGuard, logger, context, "Disable",
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
internal sealed class ArchiveEnableHandler(ISystemContext systemContext, ArchiveMethodCallerTenantGuard tenantGuard,
    ILogger<ArchiveEnableHandler> logger)
    : ICkMethodHandler<ArchiveEnableParameters, ArchiveEnableResult>
{
    public async Task<CkMethodOutcome<ArchiveEnableResult>> HandleAsync(CkMethodContext<ArchiveEnableParameters> context,
        CancellationToken cancellationToken)
    {
        var outcome = await ArchiveTransitions.RunAsync(systemContext, tenantGuard, logger, context, "Enable",
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
internal sealed class ArchiveRetryActivationHandler(ISystemContext systemContext, ArchiveMethodCallerTenantGuard tenantGuard,
    ILogger<ArchiveRetryActivationHandler> logger)
    : ICkMethodHandler<ArchiveRetryActivationParameters, ArchiveRetryActivationResult>
{
    public async Task<CkMethodOutcome<ArchiveRetryActivationResult>> HandleAsync(
        CkMethodContext<ArchiveRetryActivationParameters> context, CancellationToken cancellationToken)
    {
        var outcome = await ArchiveTransitions.RunAsync(systemContext, tenantGuard, logger, context, "RetryActivation",
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
internal sealed class ArchiveDeleteArchiveHandler(ISystemContext systemContext, ArchiveMethodCallerTenantGuard tenantGuard,
    ILogger<ArchiveDeleteArchiveHandler> logger)
    : ICkMethodHandler<ArchiveDeleteArchiveParameters, ArchiveDeleteArchiveResult>
{
    public async Task<CkMethodOutcome<ArchiveDeleteArchiveResult>> HandleAsync(
        CkMethodContext<ArchiveDeleteArchiveParameters> context, CancellationToken cancellationToken)
    {
        if (!tenantGuard.IsAllowed(context))
        {
            return CkMethodOutcome<ArchiveDeleteArchiveResult>.Forbidden(ArchiveMethodCallerTenantGuard.Reason);
        }

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
