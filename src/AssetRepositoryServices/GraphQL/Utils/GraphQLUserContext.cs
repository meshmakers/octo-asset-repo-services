using System.Collections.Concurrent;
using System.Security.Claims;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;

internal class GraphQlUserContext(ClaimsPrincipal? user, ITenantContext tenantContext) : Dictionary<string, object?>
{
    public ClaimsPrincipal? User { get; } = user;

    public string TenantId => TenantContext.TenantId;
    public ITenantContext TenantContext { get; } = tenantContext;

    /// <summary>
    ///     The unbounded-query guard's decisions on nested navigations, keyed by operation, mode, response path (without
    ///     list indices) and the facts of the decision, so a navigation resolved once per parent entity is logged and
    ///     counted once per operation.
    /// </summary>
    internal ConcurrentDictionary<string, NestedNavigationGuardDecision> NestedNavigationGuardDecisions { get; } =
        new(StringComparer.Ordinal);
}