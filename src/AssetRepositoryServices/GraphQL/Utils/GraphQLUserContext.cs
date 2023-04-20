using System.Collections.Generic;
using System.Security.Claims;
using Meshmakers.Octo.SystematizedData.Persistence;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;

public class GraphQLUserContext : Dictionary<string, object>
{
    public ClaimsPrincipal User { get; set; }

    public ITenantContext TenantContext { get; set; }
}
