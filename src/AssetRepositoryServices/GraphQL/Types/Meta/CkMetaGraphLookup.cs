using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Meta;

/// <summary>
///     CK v2 meta introspection (F1.5-S3, AB#5922): the CK meta DTOs of the SDK (<c>CkTypeDto</c>, <c>CkRecordDto</c>, …)
///     carry no CK v2 members, so the new meta fields read them from the tenant's CK cache graph. A graph that is not
///     in the cache yields the v1 defaults (public, any, no interfaces, no methods).
/// </summary>
internal static class CkMetaGraphLookup
{
    public static string TenantId(IResolveFieldContext context) => ((GraphQlUserContext)context.UserContext).TenantId;

    public static CkTypeGraph? Type(IResolveFieldContext context, CkId<CkTypeId> ckTypeId)
    {
        return context.GetCkCacheService().TryGetCkType(TenantId(context), ckTypeId, out var graph) ? graph : null;
    }

    public static CkRecordGraph? Record(IResolveFieldContext context, CkId<CkRecordId> ckRecordId)
    {
        return context.GetCkCacheService().TryGetCkRecord(TenantId(context), ckRecordId, out var graph) ? graph : null;
    }

    public static CkEnumGraph? Enum(IResolveFieldContext context, CkId<CkEnumId> ckEnumId)
    {
        return context.GetCkCacheService().TryGetCkEnum(TenantId(context), ckEnumId, out var graph) ? graph : null;
    }

    public static CkAttributeGraph? Attribute(IResolveFieldContext context, CkId<CkAttributeId> ckAttributeId)
    {
        return TryGet(() => context.GetCkCacheService().GetCkAttribute(TenantId(context), ckAttributeId));
    }

    public static CkAssociationRoleGraph? AssociationRole(IResolveFieldContext context,
        CkId<CkAssociationRoleId> ckAssociationRoleId)
    {
        return TryGet(() =>
            context.GetCkCacheService().GetCkAssociationRole(TenantId(context), ckAssociationRoleId));
    }

    public static CkInterfaceGraph? Interface(ICkCacheService ckCacheService, string tenantId,
        CkId<CkInterfaceId> ckInterfaceId)
    {
        return TryGet(() => ckCacheService.GetRtCkInterface(tenantId, ckInterfaceId.ToRtCkId()));
    }

    public static IReadOnlyList<CkInterfaceGraph> Interfaces(IResolveFieldContext context,
        IEnumerable<CkId<CkInterfaceId>> ids)
    {
        var ckCacheService = context.GetCkCacheService();
        var tenantId = TenantId(context);
        return ids.Select(id => Interface(ckCacheService, tenantId, id)).OfType<CkInterfaceGraph>().ToList();
    }

    private static T? TryGet<T>(Func<T> get) where T : class
    {
        try
        {
            return get();
        }
        catch (CkCacheException)
        {
            return null;
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }
}
