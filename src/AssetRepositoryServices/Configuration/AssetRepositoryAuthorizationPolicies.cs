using System.Security.Claims;
using Duende.IdentityModel;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Services.Infrastructure;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration;

/// <summary>
///     The authorization policies of the asset repository, in one place so the host and the tests use the very
///     same definitions.
/// </summary>
internal static class AssetRepositoryAuthorizationPolicies
{
    /// <summary>
    ///     Registers the policies.
    /// </summary>
    /// <remarks>
    ///     <see cref="AssetRepositoryServiceConstants.TenantAdministrationPolicy" /> (AB#4497) is the full-access scope plus
    ///     the tenant role <c>TenantManagement</c>. The role is one <c>role</c> claim per role name in the token; the JWT
    ///     bearer handler may rename it to <see cref="ClaimTypes.Role" />, so both spellings are probed (platform convention,
    ///     see the bot and identity services). A token without the role gets <c>403</c>.
    /// </remarks>
    public static void AddAssetRepositoryPolicies(this AuthorizationOptions options)
    {
        options.AddPolicy(AssetRepositoryServiceConstants.AuthenticatedUserPolicyGraphApi, policy =>
        {
            policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme,
                CookieAuthenticationDefaults.AuthenticationScheme);
            policy.RequireAuthenticatedUser();
        });

        options.AddPolicy(AssetRepositoryServiceConstants.AuthenticatedUserPolicy,
            policy => { policy.RequireAuthenticatedUser(); });

        options.AddPolicy(AssetRepositoryServiceConstants.SystemAssetApiReadOnlyPolicy,
            authorizationPolicyBuilder =>
            {
                authorizationPolicyBuilder.RequireClaim(InfrastructureCommon.ClaimScope,
                    CommonConstants.OctoApiFullAccess,
                    CommonConstants.OctoApiReadOnly);
            });

        options.AddPolicy(AssetRepositoryServiceConstants.SystemAssetApiReadWritePolicy,
            authorizationPolicyBuilder =>
            {
                authorizationPolicyBuilder.RequireClaim(InfrastructureCommon.ClaimScope,
                    CommonConstants.OctoApiFullAccess);
            });

        options.AddPolicy(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy,
            authorizationPolicyBuilder =>
            {
                authorizationPolicyBuilder.RequireClaim(InfrastructureCommon.ClaimScope,
                    CommonConstants.OctoApiFullAccess);
            });

        options.AddPolicy(AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy,
            authorizationPolicyBuilder =>
            {
                authorizationPolicyBuilder.RequireClaim(InfrastructureCommon.ClaimScope,
                    CommonConstants.OctoApiFullAccess,
                    CommonConstants.OctoApiReadOnly);
            });

        options.AddPolicy(AssetRepositoryServiceConstants.TenantAdministrationPolicy,
            authorizationPolicyBuilder =>
            {
                authorizationPolicyBuilder.RequireClaim(InfrastructureCommon.ClaimScope,
                        CommonConstants.OctoApiFullAccess)
                    .RequireAssertion(context => HasTenantRole(context.User, CommonConstants.TenantManagementRole));
            });

        options.AddPolicy(AssetRepositoryServiceConstants.DataModelManagementPolicy,
            authorizationPolicyBuilder =>
            {
                authorizationPolicyBuilder.RequireClaim(InfrastructureCommon.ClaimScope,
                    CommonConstants.OctoApiFullAccess,
                    CommonConstants.OctoApiDataModelManagement);
            });
    }

    /// <summary>
    ///     Whether the principal carries the tenant role under the identity's role claim type, the raw JWT
    ///     <c>role</c> claim or the inbound-mapped <see cref="ClaimTypes.Role" /> claim.
    /// </summary>
    internal static bool HasTenantRole(ClaimsPrincipal principal, string roleName)
    {
        return principal.IsInRole(roleName) ||
               principal.Claims.Any(c =>
                   (c.Type == JwtClaimTypes.Role || c.Type == ClaimTypes.Role) &&
                   string.Equals(c.Value, roleName, StringComparison.Ordinal));
    }
}
