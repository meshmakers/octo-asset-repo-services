using System.Security.Claims;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.SystemApi.v1.Controllers;

/// <summary>
///     The system-level caller check of the system endpoints that cross tenant boundaries (GridFS orphan sweep,
///     tenant registry reconcile): fail closed unless the token is signed in to the system tenant and, for a user token,
///     carries the admin panel role.
/// </summary>
internal static class SystemAdministratorGuard
{
    private const string TenantIdClaimType = "tenant_id";

    internal static bool IsSystemAdministrator(ClaimsPrincipal user, ISystemContext systemContext)
    {
        var tokenTenantId = user.FindFirstValue(TenantIdClaimType);
        if (string.IsNullOrEmpty(tokenTenantId) ||
            tokenTenantId.NormalizeString() != systemContext.TenantId.NormalizeString())
        {
            return false;
        }

        var isUserToken = user.FindFirst("sub") != null || user.FindFirst(ClaimTypes.NameIdentifier) != null;
        return !isUserToken ||
               user.Identities.SelectMany(i => i.FindAll(i.RoleClaimType).Concat(i.FindAll("role")))
                   .Any(c => c.Value == CommonConstants.AdminPanelManagementRole);
    }
}
