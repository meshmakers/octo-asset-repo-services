using System.Security.Claims;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;

internal class GraphQlUserContext(ClaimsPrincipal? user, ITenantContext tenantContext) : Dictionary<string, object?>
{
    public ClaimsPrincipal? User { get; } = user;

    public string TenantId => TenantContext.TenantId;
    public ITenantContext TenantContext { get; } = tenantContext;
}