using System.Security.Claims;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Authorization;
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
    ///     the tenant role <c>TenantManagement</c>, defined centrally by <c>AddTenantRolePolicy</c> (octo-common-services,
    ///     AB#7319: both <c>role</c> claim spellings are probed). A token without the role gets <c>403</c> with a reason.
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

        // The central scope-plus-role definition of octo-common-services (AB#7319); the host registers its handlers
        // with AddOctoTenantRoleAuthorization().
        options.AddTenantRolePolicy(AssetRepositoryServiceConstants.TenantAdministrationPolicy,
            CommonConstants.TenantManagementRole);

        options.AddPolicy(AssetRepositoryServiceConstants.DataModelManagementPolicy,
            authorizationPolicyBuilder =>
            {
                authorizationPolicyBuilder.RequireClaim(InfrastructureCommon.ClaimScope,
                    CommonConstants.OctoApiFullAccess,
                    CommonConstants.OctoApiDataModelManagement);
            });
    }
}
