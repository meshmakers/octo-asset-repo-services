using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;

/// <summary>
///     Identifies one load of a tenant's CK cache, so a GraphQL schema can tell whether it was built from the CK cache
///     that is loaded now (AB#8667).
/// </summary>
/// <remarks>
///     <para>
///         The schema used to be dropped only by a <c>PosUpdateTenant</c> event. Every other path that reloads the CK
///         cache - the AB#5415 stale-type fallback, the display-rule sweeper's refresh, the import-time unload in
///         another code path, and a <c>PosUpdateTenant</c> published while this process did not listen to the bus yet
///         (service start) - left a schema built from the old model in place until a manual ClearCache.
///     </para>
///     <para>
///         Every load of a tenant cache installs a freshly resolved model graph, so the type collection instance that
///         <see cref="ICkCacheService.GetCkTypes" /> returns marks the load - the same marker the hidden-name cache of
///         <c>AccessQueryGuard</c> keys on (review M8). Comparing it is a dictionary lookup and a reference compare: no
///         cost on the request path while the cache stays loaded, and no rebuild for tenants whose cache was not
///         reloaded.
///     </para>
/// </remarks>
internal static class CkCacheGeneration
{
    /// <summary>
    ///     Returns a marker of the CK cache currently loaded for <paramref name="tenantId" />, or <c>null</c> when none is
    ///     loaded.
    /// </summary>
    public static object? Capture(ICkCacheService ckCacheService, string tenantId)
    {
        if (!ckCacheService.IsTenantLoaded(tenantId))
        {
            return null;
        }

        try
        {
            return ckCacheService.GetCkTypes(tenantId);
        }
        catch (CkCacheException)
        {
            // Unloaded between the two calls, or created but not filled yet.
            return null;
        }
    }

    /// <summary>
    ///     True when the CK cache loaded now is not the one <paramref name="builtFrom" /> was captured from: it was
    ///     unloaded or reloaded since. A schema built without a marker (the cache was unloaded again while it was built) is
    ///     reported stale too, so the next request builds it again.
    /// </summary>
    public static bool HasChanged(ICkCacheService ckCacheService, string tenantId, object? builtFrom)
    {
        return builtFrom == null || !ReferenceEquals(builtFrom, Capture(ckCacheService, tenantId));
    }
}
