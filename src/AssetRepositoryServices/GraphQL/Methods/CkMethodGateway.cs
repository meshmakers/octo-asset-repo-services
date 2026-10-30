using System.Diagnostics;
using System.Security.Claims;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods;
using Meshmakers.Octo.Services.Methods.Audit;
using Meshmakers.Octo.Services.Methods.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Methods;

/// <summary>One call of a CK method as the gateway receives it (the arguments of a method mutation after the GraphQL layer parsed them).</summary>
/// <param name="TenantId">Tenant of the request.</param>
/// <param name="CkTypeId">Concrete CK type of the target (<c>Model/Type</c>).</param>
/// <param name="MethodId">Qualified id of the method (<c>Model/Type.Method-1</c>).</param>
/// <param name="TargetRtId">Runtime id of the target entity as the client sent it; <c>null</c> for a static method.</param>
/// <param name="ParametersJson">The parameters as a JSON object. Sensitive: never logged.</param>
/// <param name="User">The authenticated user of the HTTP request.</param>
/// <param name="AccessToken">The raw access token of the request, forwarded to the handler for its own validation. Sensitive.</param>
/// <param name="CorrelationId">Id of the request that causes the call.</param>
/// <param name="IdempotencyKey">AB#7825: the optional idempotency key of the call (the <c>idempotencyKey</c> argument), forwarded unchanged into the request; the idempotency filter of the invocation pipeline validates it. Sensitive-ish: never logged.</param>
public sealed record CkMethodGatewayCall(
    string TenantId,
    string CkTypeId,
    string MethodId,
    string? TargetRtId,
    string ParametersJson,
    ClaimsPrincipal User,
    string? AccessToken = null,
    Guid? CorrelationId = null,
    string? IdempotencyKey = null)
{
    /// <summary>Redacting: the parameters and the token never show.</summary>
    public override string ToString() => $"CkMethodGatewayCall {{ {MethodId} {TenantId} }}";
}

/// <summary>
///     The gateway of CK method calls (AB#6912, F3.4): the one place that decides, <b>before the target entity is
///     loaded</b>, whether a caller may call a method, and then dispatches through <see cref="ICkMethodDispatcher" />.
/// </summary>
/// <remarks>
///     Order: authorize with the shared <see cref="ICkMethodAuthorizationEvaluator" /> (the same instance and rules as the
///     pipeline filter at band 200: roles, <c>allowSelf</c> on the id from the request, scopes, <c>octo_api</c> always,
///     default-deny), then load the target with the caller's rights and check the data permission (AB#7037:
///     <c>Write</c>, or <c>Read</c> if the method declares <c>authorization.dataAccess: Read</c>; <c>allowSelf</c> does not
///     exempt it; a hidden target is <c>METHOD_TARGET_NOT_FOUND</c>, a visible protected one <c>METHOD_FORBIDDEN</c>), then validate the parameters (<c>METHOD_VALIDATION_FAILED</c>, AB#7026), then
///     dispatch. A refusal is <c>METHOD_FORBIDDEN</c> with the method id and a
///     fixed reason and is a pure function of the method: it is the same whether the target exists or not, and the gate
///     does not touch the target before. <c>METHOD_TARGET_NOT_FOUND</c> is only returned to a caller who passed the
///     check. The scope <c>octo_api</c> is required for method calls independent of <c>GraphQl:EnforceMutationScope</c>
///     (that flag belongs to the generic <c>MutationScopeListener</c>; the evaluator does not read it).
///     The pipeline filter stays as the second line for callers that do not pass this gateway.
/// </remarks>
public interface ICkMethodGateway
{
    /// <summary>
    ///     Runs the call. Never throws for a refused or failing call; only cancellation surfaces.
    /// </summary>
    /// <param name="call">The call.</param>
    /// <param name="targetAccess">
    ///     Loads the target with the rights of the caller and judges the data permission (after authorization only; never
    ///     called for a refused call). For a static method it judges the declaring type. Required: there is no way to skip
    ///     the data-permission check.
    /// </param>
    /// <param name="cancellationToken">The caller's token.</param>
    Task<CkMethodResponse> InvokeAsync(CkMethodGatewayCall call, CkMethodTargetAccessCheck targetAccess,
        CancellationToken cancellationToken = default);
}

internal sealed class CkMethodGateway(
    ICkMethodAuthorizationEvaluator evaluator,
    ICkMethodAuthorizationDefinitionSource definitionSource,
    ICkMethodTimeoutResolver timeoutResolver,
    ICkMethodDispatcher dispatcher,
    ICkMethodAuditRecorder auditRecorder,
    ILogger<CkMethodGateway> logger,
    IOptions<CkMethodLimitsOptions>? limits = null) : ICkMethodGateway
{
    public async Task<CkMethodResponse> InvokeAsync(CkMethodGatewayCall call, CkMethodTargetAccessCheck targetAccess,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(targetAccess);

        var invocationId = Guid.NewGuid();
        var started = Stopwatch.GetTimestamp();
        var principal = call.User.Identity?.IsAuthenticated == true ? CkMethodPrincipal.FromClaimsPrincipal(call.User) : null;

        try
        {
            return await InvokeCoreAsync(call, targetAccess, invocationId, started, principal, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // AB#7907: a fault of the gateway itself (the target-access check, the validator, the dispatcher that throws
            // against its contract) is no refusal and no answer, but the call must not vanish from the audit. It is recorded
            // once here and rethrown, so the caller gets exactly the error it got before. The real dispatcher never throws
            // for a failing call (the pipeline audits those through the band-100 filter); only a cancellation by the caller
            // is not a fault and passes through unrecorded.
            logger.LogError(
                "CK method {MethodId} invocation {InvocationId} in tenant {TenantId}: the gateway faulted ({ExceptionType}), recorded in the audit",
                call.MethodId, invocationId, call.TenantId, e.GetType().Name);
            await RecordAsync(call, invocationId, principal, started, CkMethodErrorCodes.MethodHandlerFailed)
                .ConfigureAwait(false);
            throw;
        }
    }

    private async Task<CkMethodResponse> InvokeCoreAsync(CkMethodGatewayCall call, CkMethodTargetAccessCheck targetAccess,
        Guid invocationId, long started, CkMethodPrincipal? principal, CancellationToken cancellationToken)
    {
        // 1. authorize. Nothing about the target has been read; the answer depends on the method and the caller only.
        var definition = definitionSource.Find(call.TenantId, call.CkTypeId, call.MethodId);
        var decision = definition == null
            ? CkMethodAuthorizationDecision.Deny(CkMethodDenial.UnknownMethod)
            : evaluator.Evaluate(definition.Authorization, definition.Kind, call.TargetRtId, principal);
        if (!decision.Allowed)
        {
            // The log names the rule and the method, never the target, the parameters or the token.
            logger.LogInformation(
                "CK method {MethodId} invocation {InvocationId} refused at the gateway in tenant {TenantId}: {Denial}",
                call.MethodId, invocationId, call.TenantId, decision.Denial);
            return await RefuseAsync(call, invocationId, principal, started,
                CkMethodResponse.Failed(invocationId, string.Empty, CkMethodAuthorizationErrors.Forbidden(call.MethodId)))
                .ConfigureAwait(false);
        }

        // 1b. the size of the parameters (AB#7665, F-2): after authorization (a caller who may not call the method learns
        // nothing about it), before the target is loaded and long before the parameters are parsed or validated.
        if (CkMethodParameterLimit.Exceeds(call.ParametersJson, limits?.Value, out var actualBytes))
        {
            logger.LogWarning(
                "CK method {MethodId} invocation {InvocationId} in tenant {TenantId}: parameters of {ActualBytes} bytes exceed the limit, refused",
                call.MethodId, invocationId, call.TenantId, actualBytes);
            return await RefuseAsync(call, invocationId, principal, started,
                CkMethodResponse.Failed(invocationId, string.Empty,
                    CkMethodParameterLimit.TooLarge(call.MethodId, limits?.Value, actualBytes))).ConfigureAwait(false);
        }

        // 2. load the target with the caller's rights and check the data permission (AB#7037). The data action is Write
        // unless the method declares Read. allowSelf does not exempt this check. A target the caller cannot read is
        // answered exactly like a missing one; a visible target the caller may not change is METHOD_FORBIDDEN with a
        // fixed reason. A static method has no target: only the declaring type is judged. A fault in the check fails the
        // call (the exception surfaces), it never lets it pass.
        var dataAccess = CkMethodAuthorization.Resolve(definition!.Authorization);
        var access = await targetAccess(dataAccess, cancellationToken).ConfigureAwait(false);
        switch (access.Outcome)
        {
            case CkMethodTargetAccessOutcome.Allowed:
                if (access.AuditOnlyViolation)
                {
                    // AuditOnly policy: the call passes and the signal is logged (method and action only, never the target).
                    logger.LogInformation(
                        "CK method {MethodId} invocation {InvocationId} in tenant {TenantId}: data permission would refuse {DataAccess} access (audit only)",
                        call.MethodId, invocationId, call.TenantId, dataAccess);
                }

                break;
            case CkMethodTargetAccessOutcome.NotVisible when call.TargetRtId != null:
                logger.LogInformation(
                    "CK method {MethodId} invocation {InvocationId} in tenant {TenantId}: target not found",
                    call.MethodId, invocationId, call.TenantId);
                return await RefuseAsync(call, invocationId, principal, started,
                    CkMethodResponse.Failed(invocationId, string.Empty,
                        CkMethodError.Create(CkMethodErrorCodes.MethodTargetNotFound,
                            (CkMethodErrorParameters.MethodId, call.MethodId)))).ConfigureAwait(false);
            default:
                // NotPermitted, or a static method whose declaring type the caller cannot read at all.
                logger.LogInformation(
                    "CK method {MethodId} invocation {InvocationId} refused by a data permission in tenant {TenantId} ({DataAccess})",
                    call.MethodId, invocationId, call.TenantId, dataAccess);
                return await RefuseAsync(call, invocationId, principal, started,
                    CkMethodResponse.Failed(invocationId, string.Empty, CkMethodDataPermissionErrors.Forbidden(call.MethodId)))
                    .ConfigureAwait(false);
        }

        // 3. validate the parameters against the declaration. After authorization and the target lookup, so a caller who
        // may not call the method learns nothing about its parameters, and before dispatch, so nothing invalid leaves.
        // The error names the members and a fixed reason, never a value.
        var invalid = CkMethodParameterValidator.Validate(call.MethodId, definition, call.ParametersJson);
        if (invalid != null)
        {
            logger.LogInformation(
                "CK method {MethodId} invocation {InvocationId} in tenant {TenantId}: parameters rejected ({Reason})",
                call.MethodId, invocationId, call.TenantId, invalid.Parameters.GetValueOrDefault(CkMethodErrorParameters.Reason));
            return await RefuseAsync(call, invocationId, principal, started,
                CkMethodResponse.Failed(invocationId, string.Empty, invalid)).ConfigureAwait(false);
        }

        // 4. dispatch. The principal travels in the ambient scope, so the filter (second line) judges the same caller.
        var request = new CkMethodRequest(call.TenantId)
        {
            InvocationId = invocationId,
            CorrelationId = call.CorrelationId,
            MethodId = call.MethodId,
            CkTypeId = call.CkTypeId,
            TargetRtId = call.TargetRtId,
            ParametersJson = call.ParametersJson,
            Caller = CallerOf(call.User, principal!),
            CallerAccessToken = call.AccessToken,
            IdempotencyKey = call.IdempotencyKey,
            DeadlineUtc = timeoutResolver.GetDeadlineUtc(call.TenantId, call.CkTypeId, call.MethodId)
        };
        using (CkMethodPrincipalScope.Begin(principal!))
        {
            return await dispatcher.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     AB#7826: a call the gateway refuses never reaches the invocation pipeline, so the audit filter (band 100) never sees
    ///     it. The gateway records it through the same <see cref="ICkMethodAuditRecorder" />: transport <c>gateway</c>, the
    ///     invocation id minted above, the error code of the answer as outcome, no handler instance. The record has no place
    ///     for parameters, the idempotency key or the token. A call that passes the gateway is recorded by the filter only, so
    ///     there is exactly one event per invocation id. A refusal is a call that did not run: when the tenant fails calls on an
    ///     audit failure the answer stays the refusal (the recorder has logged and counted the failure). The event is written
    ///     with a token that is not cancelled, like the filter does.
    ///     AB#7907: a <b>fault</b> (an exception from the gateway's own steps or from a dispatcher that throws) is recorded the
    ///     same way with outcome <c>METHOD_HANDLER_FAILED</c> (the audit filter uses the same outcome for an exception inside the
    ///     pipeline; transport <c>gateway</c> and no handler instance say that it happened before the handler) and the
    ///     exception surfaces unchanged. A cancellation by the caller is not a fault and is not recorded here.
    /// </summary>
    private async Task<CkMethodResponse> RefuseAsync(CkMethodGatewayCall call, Guid invocationId, CkMethodPrincipal? principal,
        long started, CkMethodResponse refusal)
    {
        await RecordAsync(call, invocationId, principal, started, CkMethodAuditRecord.OutcomeOf(refusal)).ConfigureAwait(false);
        return refusal;
    }

    /// <summary>
    ///     Writes the gateway event with the given outcome and never lets a failing recorder change what the caller gets
    ///     (the refusal, or the exception of a fault).
    /// </summary>
    private async Task RecordAsync(CkMethodGatewayCall call, Guid invocationId, CkMethodPrincipal? principal, long started,
        string outcome)
    {
        var record = new CkMethodAuditRecord
        {
            TenantId = call.TenantId,
            InvocationId = invocationId,
            MethodId = call.MethodId,
            CkTypeId = call.CkTypeId,
            TargetRtId = call.TargetRtId,
            Transport = CkMethodAuditRecord.GatewayTransport,
            Outcome = outcome,
            CallerSubject = principal?.SubjectId ?? call.User.FindFirst("client_id")?.Value,
            CallerName = call.User.FindFirst("name")?.Value,
            Duration = Stopwatch.GetElapsedTime(started)
        };
        try
        {
            await auditRecorder.RecordAsync(record, CancellationToken.None).ConfigureAwait(false);
        }
        catch (CkMethodAuditFailedException)
        {
            // logged and counted by the recorder; the call did not run, so it stays the refusal (or the fault) it was
        }
        catch (Exception e)
        {
            // a custom recorder must not turn a refusal into a fault
            logger.LogError(e, "CK method {MethodId} invocation {InvocationId}: the audit recorder threw", call.MethodId,
                invocationId);
        }
    }

    private static CkMethodCaller CallerOf(ClaimsPrincipal user, CkMethodPrincipal principal) => new()
    {
        SubjectId = principal.SubjectId,
        ClientId = user.FindFirst("client_id")?.Value,
        TenantId = user.FindFirst("tenant_id")?.Value,
        Name = user.FindFirst("name")?.Value,
        Roles = principal.Roles.ToArray()
    };
}
