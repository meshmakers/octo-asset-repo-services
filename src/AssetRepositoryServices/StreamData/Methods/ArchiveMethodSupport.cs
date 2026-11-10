using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Meshmakers.Octo.Services.Methods;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Methods;

/// <summary>
///     The error codes of the System.StreamData archive methods (AB#8598). The STREAMDATA_* codes are the codes the streamData
///     mutations answer today (<see cref="Statics" />), so a client that switches keeps its error handling; the CK model
///     declares every one of them on every archive method.
/// </summary>
internal static class ArchiveMethodErrors
{
    /// <summary>Stream data is not enabled for the tenant (the mutation answers this with the generic code ASSET1002).</summary>
    public const string StreamDataNotEnabled = "STREAMDATA_NOT_ENABLED";

    /// <summary>Error parameter with the engine's message, as the GraphQL error of the mutation carries it.</summary>
    public const string MessageParameter = "message";

    /// <summary>
    ///     The code of a stream-data exception: exactly the mapping of the mutations
    ///     (<c>ResolveConnectionContextExtensions.HandleStreamDataException</c>).
    /// </summary>
    public static string CodeOf(StreamDataException exception) => exception switch
    {
        ArchiveNotFoundException => Statics.GraphQlErrorStreamDataArchiveNotFound,
        ArchiveNotActivatedException => Statics.GraphQlErrorStreamDataArchiveNotActivated,
        InvalidArchiveStateTransitionException => Statics.GraphQlErrorStreamDataInvalidTransition,
        ArchiveSchemaImmutableException or RollupSchemaImmutableException => Statics.GraphQlErrorStreamDataSchemaImmutable,
        ArchivePathInvalidException => Statics.GraphQlErrorStreamDataPathInvalid,
        ArchiveActivationFailedException => Statics.GraphQlErrorStreamDataActivationFailed,
        _ => Statics.GraphQlErrorStreamData
    };

    /// <summary>
    ///     The declared failure of a stream-data exception with the parameters the mutation puts into the error extensions
    ///     (<c>archiveRtId</c>, <c>actualStatus</c>, <c>fromStatus</c>, <c>attemptedTransition</c>, <c>path</c>) and the engine's
    ///     message. None of them is a sensitive value: the archive methods have no sensitive parameter.
    /// </summary>
    public static CkMethodFailure FailureOf(StreamDataException exception)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MessageParameter] = exception.Message
        };
        if (exception.ArchiveRtId is { } archiveRtId)
        {
            parameters["archiveRtId"] = archiveRtId.ToString();
        }

        switch (exception)
        {
            case ArchiveNotActivatedException notActivated:
                parameters["actualStatus"] = notActivated.ActualStatus.ToString();
                break;
            case InvalidArchiveStateTransitionException invalidTransition:
                parameters["fromStatus"] = invalidTransition.FromStatus.ToString();
                parameters["attemptedTransition"] = invalidTransition.AttemptedTransition;
                break;
            case ArchivePathInvalidException pathInvalid:
                parameters["path"] = pathInvalid.Path;
                break;
        }

        return new CkMethodFailure(CodeOf(exception), parameters);
    }
}

/// <summary>Steps the archive method handlers share.</summary>
internal static class ArchiveMethodSupport
{
    /// <summary>The target as object id; <c>false</c> for a missing or malformed id (it cannot exist).</summary>
    public static bool TryGetTarget(CkMethodContext context, out OctoObjectId target)
    {
        target = default;
        return !string.IsNullOrWhiteSpace(context.TargetRtId) && OctoObjectId.TryParse(context.TargetRtId, out target);
    }

    /// <summary>
    ///     The tenant context of the call. An in-process handler runs outside the request session (AB#7023), so it resolves
    ///     the tenant from the call, never from ambient state; the invocation pipeline has already checked that the caller's
    ///     token belongs to this tenant.
    /// </summary>
    public static async Task<ITenantContext?> FindTenantAsync(ISystemContext systemContext, CkMethodContext context) =>
        await systemContext.TryFindTenantContextAsync(context.TenantId).ConfigureAwait(false);
}

/// <summary>
///     Refuses a call whose caller token was issued for another tenant (AB#8598). An in-process call is not re-validated by
///     the bus host (CkMethodCallerTokenValidator), and the HTTP gate (<c>TenantAuthorizationMiddleware</c>) may run in LogOnly;
///     the archive methods therefore apply the claim rule of the bus host themselves: a user token (with <c>sub</c>) must carry
///     the call's tenant in <c>tenant_id</c>; a service token (no <c>sub</c>) must carry it too or be on the cross-tenant
///     allow-list <c>TenantAuthorization:CrossTenantServiceClientIds</c>. Everything else is <c>METHOD_FORBIDDEN</c> with the
///     reason <see cref="Reason" />, before the archive is touched.
/// </summary>
internal sealed class ArchiveMethodCallerTenantGuard(IOptions<TenantAuthorizationOptions> tenantOptions)
{
    /// <summary>The reason parameter of the refusal.</summary>
    public const string Reason = "tenant-not-allowed";

    /// <summary><c>true</c> when the caller may act in the call's tenant.</summary>
    public bool IsAllowed(CkMethodContext context)
    {
        var caller = context.Caller;
        if (caller == null)
        {
            return false;
        }

        var tenantMatches = !string.IsNullOrEmpty(caller.TenantId) &&
                            string.Equals(caller.TenantId, context.TenantId, StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(caller.SubjectId))
        {
            return tenantMatches;
        }

        return !string.IsNullOrWhiteSpace(caller.ClientId) &&
               (tenantMatches || tenantOptions.Value.IsCrossTenantServiceClient(caller.ClientId));
    }
}
