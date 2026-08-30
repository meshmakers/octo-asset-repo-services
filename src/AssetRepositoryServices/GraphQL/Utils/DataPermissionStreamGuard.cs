using GraphQL;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.DataPermissions;

using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;

/// <summary>
///     Stream-data type gate (AB#4973, decision F4): a stream-data read on a protected CK type is
///     rejected before any CrateDB SQL is built when the caller has no full Read grant. Stream rows
///     carry no creator, so an owned-only grant cannot be honored per row and denies stream reads
///     (conservative). Tenants without policies pass through untouched.
/// </summary>
internal static class DataPermissionStreamGuard
{
    internal static async Task EnsureStreamReadAllowedAsync(IResolveFieldContext ctx, GraphQlUserContext gql,
        RtCkId<CkTypeId> ckTypeId)
    {
        var securityContext = Helpers.GetSecurityContext(gql);
        if (securityContext.IsSystem)
        {
            return;
        }

        var resolver = ctx.RequestServices?.GetService<IDataPermissionResolver>();
        var ckCacheService = ctx.RequestServices?.GetService<ICkCacheService>();
        if (resolver == null || ckCacheService == null)
        {
            return;
        }

        var tenantRepository = gql.TenantContext.GetTenantRepository();
        var policyTable = await resolver.GetPolicyTableAsync(tenantRepository).ConfigureAwait(false);
        if (!policyTable.HasRules)
        {
            return;
        }

        var selfAndBase =
            RtDataPermissionCkTypeHelper.GetSelfAndBaseFullNames(ckCacheService, gql.TenantId, ckTypeId);
        var level = RtDataAccessEvaluator.Classify(policyTable, selfAndBase, RtDataAction.Read, securityContext,
            includeAuditOnlyPolicies: false);
        if (level is RtDataAccessLevel.Denied or RtDataAccessLevel.OwnedOnly)
        {
            throw new ExecutionError(
                $"Access denied: missing data permission 'Read' on '{ckTypeId.SemanticVersionedFullName}' for stream data.")
            {
                Code = Statics.GraphQlForbidden
            };
        }
    }
}
