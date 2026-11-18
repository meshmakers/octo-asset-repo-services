using System.Diagnostics;
using System.Security.Claims;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Methods;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.DataPermissions;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Meshmakers.Octo.Services.Methods;
using Meshmakers.Octo.Services.Methods.Audit;
using Meshmakers.Octo.Services.Methods.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Methods;

/// <summary>Why an archive call does not run through its CK method (the reason of the fallback WARN line and the docs).</summary>
internal enum ArchiveMethodFallbackReason
{
    /// <summary><c>Methods:StreamData:Enabled=false</c>: no archive method handler is hosted.</summary>
    SwitchOff,

    /// <summary>The tenant's CK model does not declare the method (System.StreamData older than the method, or not resolved).</summary>
    MethodNotDeclared,

    /// <summary>
    ///     The endpoint is not a facade of the method at all (the deprecated <c>recomputeArchive</c> and
    ///     <c>backfillRollupFromSource</c> mutations): it always calls the lifecycle service directly.
    /// </summary>
    NotRouted
}

/// <summary>The result of a guarded direct call: the refusal (the envelope the method path would answer) or the value.</summary>
internal readonly record struct ArchiveMethodFallbackResult<T>(CkMethodResponse? Refusal, T? Value)
{
    /// <summary><c>true</c> when the guard refused the call; the direct call did not run.</summary>
    public bool IsRefused => Refusal != null;
}

/// <summary>
///     The guard of every archive lifecycle, rollup and recompute call that does <b>not</b> run through its CK method -
///     the REST endpoints and deprecated mutations while <see cref="IArchiveMethodFacade.CanServe" /> is false (switch off, or
///     the method is not declared in the tenant's CK model), and the <c>recomputeArchive</c> / <c>backfillRollupFromSource</c>
///     mutations, which always call the service directly. Before the direct call it applies the same checks as the method
///     path, with the same rules and answers: the caller tenant (<c>CkMethodCallerTenantCheck</c>, including
///     <c>TenantAuthorization:CrossTenantServiceClientIds</c> and the parent-tenant rule, independent of the HTTP tenant
///     gate's mode) and the data permission on the archive (loaded with the caller's rights under the declaring CK type, like
///     the facade does; the data action the method declares, otherwise <c>Write</c>). It writes exactly one Method audit
///     event per call with transport <see cref="ArchiveMethodFallbackGuard.Transport" /> (also for a refusal or a fault) and one WARN line naming the
///     operation, the method id and the reason - never the target id, parameters, user or token.
/// </summary>
/// <remarks>
///     The declared role is not judged here: the REST policy (<c>StreamDataAdministrationPolicy</c>) and the mutations' role
///     check refuse a caller without <c>StreamDataAdmin</c> before the guard runs. Parameter validation is not repeated: the
///     direct call validates its own input as before. There is no option to turn the guard off.
/// </remarks>
internal interface IArchiveMethodFallbackGuard
{
    /// <summary>
    ///     Checks the caller, runs <paramref name="direct" /> when it passes and records the call. Never throws for a refused
    ///     call (the refusal is the result); an exception of <paramref name="direct" /> or of the checks is recorded and
    ///     surfaces unchanged. When the tenant fails calls on an audit failure and the event of a call that ran could not be
    ///     written - the call succeeded, or the engine refused it with a <see cref="StreamDataException" /> - the result is
    ///     <c>METHOD_OUTCOME_UNKNOWN</c>, the answer of the method path; a refusal of the guard itself stays the refusal.
    /// </summary>
    /// <param name="tenant">The tenant of the request.</param>
    /// <param name="ckTypeId">The declaring CK type of the method (<see cref="ArchiveMethodFacade.ArchiveCkTypeId" /> or <see cref="ArchiveMethodFacade.RollupArchiveCkTypeId" />).</param>
    /// <param name="methodId">Qualified id of the method the call stands for.</param>
    /// <param name="operation">Name of the endpoint or mutation, for the log line.</param>
    /// <param name="targetRtId">Runtime id of the archive.</param>
    /// <param name="user">The authenticated caller of the request.</param>
    /// <param name="direct">The direct lifecycle call; runs only for a caller who passed.</param>
    /// <param name="reason">Why the call does not run through the method; <c>null</c> derives it from the switch.</param>
    /// <param name="cancellationToken">Bounds the checks before the direct call.</param>
    Task<ArchiveMethodFallbackResult<T>> InvokeAsync<T>(ITenantContext tenant, string ckTypeId, string methodId,
        string operation, OctoObjectId targetRtId, ClaimsPrincipal user, Func<Task<T>> direct,
        ArchiveMethodFallbackReason? reason = null, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
internal sealed class ArchiveMethodFallbackGuard(
    IRtDataPermissionEvaluator dataPermissions,
    ICkMethodAuthorizationDefinitionSource definitions,
    ICkMethodAuditRecorder auditRecorder,
    IOptions<StreamDataMethodsOptions> methodOptions,
    ILogger<ArchiveMethodFallbackGuard> logger,
    IOptions<TenantAuthorizationOptions>? tenantOptions = null,
    ITenantHierarchyReader? hierarchyReader = null) : IArchiveMethodFallbackGuard
{
    /// <summary>The audit transport of a call on the fallback path (the method path writes <c>in-process</c> or <c>gateway</c>).</summary>
    public const string Transport = "fallback";

    /// <inheritdoc />
    public async Task<ArchiveMethodFallbackResult<T>> InvokeAsync<T>(ITenantContext tenant, string ckTypeId,
        string methodId, string operation, OctoObjectId targetRtId, ClaimsPrincipal user, Func<Task<T>> direct,
        ArchiveMethodFallbackReason? reason = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(direct);

        var invocationId = Guid.NewGuid();
        var started = Stopwatch.GetTimestamp();
        var principal = user.Identity?.IsAuthenticated == true ? CkMethodPrincipal.FromClaimsPrincipal(user) : null;
        var why = reason ?? (methodOptions.Value.Enabled
            ? ArchiveMethodFallbackReason.MethodNotDeclared
            : ArchiveMethodFallbackReason.SwitchOff);
        T value;
        try
        {
            var refusal = await CheckAsync(tenant, ckTypeId, methodId, targetRtId, user, principal, cancellationToken)
                .ConfigureAwait(false);
            if (refusal != null)
            {
                // a refusal did not run: it stays the refusal, also when the audit write fails
                await FinishAsync(refusal.Code).ConfigureAwait(false);
                return new ArchiveMethodFallbackResult<T>(CkMethodResponse.Failed(invocationId, string.Empty, refusal),
                    default);
            }

            value = await direct().ConfigureAwait(false);
        }
        catch (StreamDataException e)
        {
            // The engine refused the call. Like the audit filter of the method pipeline, where the handler answers this
            // refusal as a STREAMDATA_* failure (an execution state the gateway cannot vouch for): when the tenant fails
            // calls on an audit failure and the event could not be written, the answer is METHOD_OUTCOME_UNKNOWN. Otherwise
            // the exception surfaces unchanged and the caller keeps its old answer.
            if (!await FinishAsync(ArchiveMethodErrors.CodeOf(e)).ConfigureAwait(false))
            {
                return OutcomeUnknown();
            }

            throw;
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(CkMethodAuditOutcomes.CallerCancelled).ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            // a fault of the checks or of the direct call: recorded, the exception surfaces unchanged
            await FinishAsync(CkMethodErrorCodes.MethodHandlerFailed).ConfigureAwait(false);
            throw;
        }

        // The call ran. Like the audit filter of the method pipeline: when the tenant fails calls on an audit failure and
        // the event could not be written, the answer is METHOD_OUTCOME_UNKNOWN (the change may have taken effect).
        return await FinishAsync(CkMethodAuditOutcomes.Success).ConfigureAwait(false)
            ? new ArchiveMethodFallbackResult<T>(null, value)
            : OutcomeUnknown();

        ArchiveMethodFallbackResult<T> OutcomeUnknown() => new(CkMethodResponse.Failed(invocationId, string.Empty,
            CkMethodError.Create(CkMethodErrorCodes.MethodOutcomeUnknown, (CkMethodErrorParameters.MethodId, methodId))),
            default);

        // exactly one WARN line and one audit event per call, whatever the outcome; false when a strict audit refused the event
        async Task<bool> FinishAsync(string outcome)
        {
            logger.LogWarning(
                "{Operation} in tenant {TenantId} ran on the fallback of {MethodId} ({Reason}), invocation {InvocationId}: {Outcome}",
                operation, tenant.TenantId, methodId, ReasonText(why), invocationId, outcome);
            return await CkMethodCallChecks.RecordAsync(auditRecorder, logger, CkMethodCallChecks.RecordOf(tenant.TenantId,
                invocationId, methodId, ckTypeId, targetRtId.ToString(), Transport, outcome, principal, user,
                Stopwatch.GetElapsedTime(started))).ConfigureAwait(false);
        }
    }

    /// <summary>The fixed token of a reason in the log line and the docs.</summary>
    public static string ReasonText(ArchiveMethodFallbackReason reason) => reason switch
    {
        ArchiveMethodFallbackReason.SwitchOff => "switch-off",
        ArchiveMethodFallbackReason.MethodNotDeclared => "method-not-declared",
        _ => "not-routed"
    };

    /// <summary>The caller tenant, then the data permission on the archive; <c>null</c> when the caller may proceed.</summary>
    private async Task<CkMethodError?> CheckAsync(ITenantContext tenant, string ckTypeId, string methodId,
        OctoObjectId targetRtId, ClaimsPrincipal user, CkMethodPrincipal? principal, CancellationToken cancellationToken)
    {
        // 1. the caller tenant: the rule of the gateway (cross-tenant service clients, parent tenant), before the archive is read
        var tenantCheck = await CkMethodCallerTenantCheck.CheckAsync(principal, tenant.TenantId, methodId,
            tenantOptions?.Value ?? new TenantAuthorizationOptions(), hierarchyReader, logger, Transport).ConfigureAwait(false);
        if (tenantCheck.IsRefused)
        {
            return tenantCheck.Error;
        }

        // 2. the data permission on the archive, with the caller's rights and the archive resolution of the facade: the
        // declaring type, the stored type of the record judged. The data action the tenant's model declares, else Write.
        var dataAccess = CkMethodAuthorization.Resolve(definitions.Find(tenant.TenantId, ckTypeId, methodId)?.Authorization);
        var access = await CkMethodTargetAccessChecker.ForInstance(tenant.GetTenantRepository(),
                Helpers.GetSecurityContext(user), dataPermissions, new RtCkId<CkTypeId>(ckTypeId), targetRtId)
            (dataAccess, cancellationToken).ConfigureAwait(false);
        return CkMethodCallChecks.RefusalOf(access, methodId, hasTarget: true);
    }
}
