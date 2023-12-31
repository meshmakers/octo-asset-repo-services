using System.Security.Claims;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;

internal class GraphQlUserContext : Dictionary<string, object?>
{
    public GraphQlUserContext(ClaimsPrincipal? user, ITenantContext tenantContext)
    {
        User = user;
        TenantContext = tenantContext;
    }

    public ClaimsPrincipal? User { get;}

    public string TenantId => TenantContext.TenantId;
    public ITenantContext TenantContext { get; }
}
