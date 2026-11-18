using System.Security.Claims;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods;
using Meshmakers.Octo.Services.Methods.Audit;
using Meshmakers.Octo.Services.Methods.Authorization;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Methods;

/// <summary>
///     The pre-dispatch rules of a CK method call that more than one caller applies (AB#9337): the answer of the
///     data-permission check and the audit event of a call that did not pass the invocation pipeline. Used by
///     <see cref="CkMethodGateway" /> and by the archive fallback guard
///     (<c>StreamData.Methods.ArchiveMethodFallbackGuard</c>), so both paths give the same answer and write the same
///     event shape for the same caller. The caller-tenant rule itself is <c>CkMethodCallerTenantCheck</c>
///     (octo-common-services), called by both.
/// </summary>
internal static class CkMethodCallChecks
{
    /// <summary>
    ///     The refusal of a data-permission check (AB#7037), <c>null</c> when the caller may proceed: a target the caller
    ///     cannot see is <c>METHOD_TARGET_NOT_FOUND</c> (answered exactly like a missing one); a visible target the caller may
    ///     not change, and a static method whose declaring type the caller cannot read, are <c>METHOD_FORBIDDEN</c> with the
    ///     fixed data-permission reason. An unknown outcome refuses.
    /// </summary>
    /// <param name="access">The verdict of the <see cref="CkMethodTargetAccessCheck" />.</param>
    /// <param name="methodId">Qualified method id.</param>
    /// <param name="hasTarget"><c>true</c> for an instance method (a target id was sent).</param>
    public static CkMethodError? RefusalOf(CkMethodTargetAccess access, string methodId, bool hasTarget) =>
        access.Outcome switch
        {
            CkMethodTargetAccessOutcome.Allowed => null,
            CkMethodTargetAccessOutcome.NotVisible when hasTarget => CkMethodError.Create(
                CkMethodErrorCodes.MethodTargetNotFound, (CkMethodErrorParameters.MethodId, methodId)),
            _ => CkMethodDataPermissionErrors.Forbidden(methodId)
        };

    /// <summary>
    ///     Writes one Method audit event for a call the invocation pipeline never saw (a refusal or fault of the gateway, a
    ///     call on the archive fallback) and never lets a failing recorder change what the caller gets. The record has no
    ///     place for parameters, the idempotency key or the token. Written with a token that is not cancelled, like the
    ///     pipeline's audit filter does.
    /// </summary>
    public static async Task RecordAsync(ICkMethodAuditRecorder recorder, ILogger logger, CkMethodAuditRecord record)
    {
        try
        {
            await recorder.RecordAsync(record, CancellationToken.None).ConfigureAwait(false);
        }
        catch (CkMethodAuditFailedException)
        {
            // logged and counted by the recorder; the answer stays what it was
        }
        catch (Exception e)
        {
            // a custom recorder must not turn an answer into a fault
            logger.LogError(e, "CK method {MethodId} invocation {InvocationId}: the audit recorder threw", record.MethodId,
                record.InvocationId);
        }
    }

    /// <summary>The audit event of a call that did not pass the invocation pipeline (no handler instance).</summary>
    public static CkMethodAuditRecord RecordOf(string tenantId, Guid invocationId, string methodId, string ckTypeId,
        string? targetRtId, string transport, string outcome, CkMethodPrincipal? principal, ClaimsPrincipal user,
        TimeSpan duration) => new()
    {
        TenantId = tenantId,
        InvocationId = invocationId,
        MethodId = methodId,
        CkTypeId = ckTypeId,
        TargetRtId = targetRtId,
        Transport = transport,
        Outcome = outcome,
        CallerSubject = principal?.SubjectId ?? user.FindFirst("client_id")?.Value,
        CallerName = user.FindFirst("name")?.Value,
        Duration = duration
    };
}
