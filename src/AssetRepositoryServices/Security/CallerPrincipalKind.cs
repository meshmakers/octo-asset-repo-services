using System.Security.Claims;
using Meshmakers.Octo.Runtime.Contracts;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Security;

/// <summary>
///     AB#8705: derives from the caller's access token whether a person or a service client acts, so that CK types with a
///     <c>writeAccess</c> block (AB#8574) are enforced for real tokens. The kind goes into every caller-scoped repository
///     session (<see cref="RtSecurityContext.PrincipalKind" />) and into the RT import job
///     (<c>ImportRtCommandRequest.InitiatedByPrincipalKind</c>).
/// </summary>
/// <remarks>
///     <para>
///         The rule relies only on claims the identity service already issues (no new claim, existing tokens keep
///         working):
///     </para>
///     <list type="number">
///         <item>
///             Not authenticated: <see cref="RtPrincipalKind.Unknown" />.
///         </item>
///         <item>
///             <c>act</c> claim present, or <c>amr</c> <c>delegation</c> / <c>impersonation</c>:
///             <see cref="RtPrincipalKind.Client" />. These are the on-behalf-of grant (a service account acts for a user,
///             e.g. a pipeline with <c>mcpDelegateToCaller</c>) and impersonation. Decision Odin 2026-11-10 on AB#8705:
///             an automated actor working on a person's behalf is a client, not the person.
///         </item>
///         <item>
///             <c>sub</c> present: <see cref="RtPrincipalKind.Interactive" />. User grants (authorization code, device
///             code, their refresh) and the cross-tenant token exchange of a person's own token (<c>amr</c>
///             <c>token_exchange</c>, no actor) carry the user's subject.
///         </item>
///         <item>
///             No <c>sub</c> but <c>client_id</c>: <see cref="RtPrincipalKind.Client" />. Client-credentials tokens carry
///             no <c>sub</c> by platform contract (the identity service strips it; the tenant gate recognises service
///             tokens by its absence).
///         </item>
///         <item>
///             Neither: <see cref="RtPrincipalKind.Unknown" /> (restricted types refuse it, all others ignore it).
///         </item>
///     </list>
///     <para>
///         Claim names are read in their JWT short form and in the form the default inbound claim mapping produces, like
///         the subject in <c>GraphQL.Helpers.GetSecurityContext</c>.
///     </para>
/// </remarks>
internal static class CallerPrincipalKind
{
    /// <summary>The RFC 8693 actor claim the identity service issues on delegated and impersonated tokens.</summary>
    internal const string ActClaimType = "act";

    /// <summary>The authentication method reference claim.</summary>
    internal const string AmrClaimType = "amr";

    /// <summary><c>amr</c> value of the on-behalf-of (delegation) grant.</summary>
    internal const string DelegationMethod = "delegation";

    /// <summary><c>amr</c> value of the impersonation grant.</summary>
    internal const string ImpersonationMethod = "impersonation";

    private const string MappedAmrClaimType = "http://schemas.microsoft.com/claims/authnmethodsreferences";

    /// <summary>
    ///     Returns the principal kind of <paramref name="user" /> by the rule in the remarks of this class.
    /// </summary>
    /// <param name="user">The authenticated caller, or null</param>
    public static RtPrincipalKind Resolve(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            return RtPrincipalKind.Unknown;
        }

        if (HasValue(user, ActClaimType) || HasValue(user, ClaimTypes.Actor) ||
            HasAuthenticationMethod(user, DelegationMethod) || HasAuthenticationMethod(user, ImpersonationMethod))
        {
            return RtPrincipalKind.Client;
        }

        if (HasValue(user, "sub") || HasValue(user, ClaimTypes.NameIdentifier))
        {
            return RtPrincipalKind.Interactive;
        }

        return HasValue(user, "client_id") ? RtPrincipalKind.Client : RtPrincipalKind.Unknown;
    }

    private static bool HasValue(ClaimsPrincipal user, string claimType)
    {
        return user.FindAll(claimType).Any(c => !string.IsNullOrWhiteSpace(c.Value));
    }

    private static bool HasAuthenticationMethod(ClaimsPrincipal user, string method)
    {
        return user.FindAll(AmrClaimType).Concat(user.FindAll(MappedAmrClaimType))
            .Any(c => string.Equals(c.Value, method, StringComparison.Ordinal));
    }
}
