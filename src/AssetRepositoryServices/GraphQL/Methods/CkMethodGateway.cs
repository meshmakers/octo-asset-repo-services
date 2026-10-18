using System.Security.Claims;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods;
using Meshmakers.Octo.Services.Methods.Authorization;
using Microsoft.Extensions.Logging;

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
public sealed record CkMethodGatewayCall(
    string TenantId,
    string CkTypeId,
    string MethodId,
    string? TargetRtId,
    string ParametersJson,
    ClaimsPrincipal User,
    string? AccessToken = null,
    Guid? CorrelationId = null)
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
///     default-deny), then load the target, then dispatch. A refusal is <c>METHOD_FORBIDDEN</c> with the method id and a
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
    /// <param name="targetExists">
    ///     Looks the target up (after authorization only). Not called for a static method or when the call is refused.
    ///     <c>null</c> skips the existence check (static methods only).
    /// </param>
    /// <param name="cancellationToken">The caller's token.</param>
    Task<CkMethodResponse> InvokeAsync(CkMethodGatewayCall call, Func<CancellationToken, Task<bool>>? targetExists,
        CancellationToken cancellationToken = default);
}

internal sealed class CkMethodGateway(
    ICkMethodAuthorizationEvaluator evaluator,
    ICkMethodAuthorizationDefinitionSource definitionSource,
    ICkMethodTimeoutResolver timeoutResolver,
    ICkMethodDispatcher dispatcher,
    ILogger<CkMethodGateway> logger) : ICkMethodGateway
{
    public async Task<CkMethodResponse> InvokeAsync(CkMethodGatewayCall call,
        Func<CancellationToken, Task<bool>>? targetExists, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);

        var invocationId = Guid.NewGuid();
        var principal = call.User.Identity?.IsAuthenticated == true ? CkMethodPrincipal.FromClaimsPrincipal(call.User) : null;

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
            return CkMethodResponse.Failed(invocationId, string.Empty, CkMethodAuthorizationErrors.Forbidden(call.MethodId));
        }

        // 2. load the target (instance methods only). Only an authorized caller can learn that it is missing.
        if (call.TargetRtId != null && targetExists != null &&
            !await targetExists(cancellationToken).ConfigureAwait(false))
        {
            logger.LogInformation(
                "CK method {MethodId} invocation {InvocationId} in tenant {TenantId}: target not found",
                call.MethodId, invocationId, call.TenantId);
            return CkMethodResponse.Failed(invocationId, string.Empty,
                CkMethodError.Create(CkMethodErrorCodes.MethodTargetNotFound,
                    (CkMethodErrorParameters.MethodId, call.MethodId)));
        }

        // 3. dispatch. The principal travels in the ambient scope, so the filter (second line) judges the same caller.
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
            DeadlineUtc = timeoutResolver.GetDeadlineUtc(call.TenantId, call.CkTypeId, call.MethodId)
        };
        using (CkMethodPrincipalScope.Begin(principal!))
        {
            return await dispatcher.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
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
