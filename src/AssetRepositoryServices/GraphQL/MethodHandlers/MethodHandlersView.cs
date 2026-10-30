using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Methods;
using Meshmakers.Octo.Services.Methods;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.MethodHandlers;

/// <summary>State of a method in the <c>MethodHandlers</c> diagnostics (AB#7837).</summary>
internal enum MethodHandlerMethodState
{
    /// <summary>At least one live handler instance, or the method is served in-process.</summary>
    Live,

    /// <summary>Handler instances are known, but none is live (expired, withdrawn or no queue bound).</summary>
    NoLiveHandler,

    /// <summary>The system tenant defines the method with a service handler, but no instance ever announced it.</summary>
    NotAnnounced
}

/// <summary>Transport a method is served over.</summary>
internal enum MethodHandlerTransport
{
    /// <summary>Message bus: handler instances in other services announce themselves.</summary>
    Bus,

    /// <summary>Served in-process by this service.</summary>
    InProcess
}

/// <summary>Which replica answered and how its registry snapshot behaves.</summary>
internal sealed record MethodHandlersAnsweredBy(
    string DispatcherInstanceId,
    string ServiceVersion,
    bool SnapshotEnabled,
    DateTime GeneratedAtUtc,
    DateTime? SnapshotLoadedAtUtc,
    int? SnapshotRowsRestored,
    bool SnapshotLoadFailed,
    DateTime? LastFlushUtc,
    bool LastFlushFailed);

/// <summary>One handler instance of a method.</summary>
internal sealed record MethodHandlerInstance(
    string ServiceName,
    string InstanceId,
    string? Version,
    int ContractVersion,
    IReadOnlyList<string> Tenants,
    DateTime LastSeenUtc,
    DateTime ExpiresUtc,
    CkMethodHandlerState State,
    CkMethodHandlerEntrySource Source,
    DateTime? PersistedLastSeenUtc);

/// <summary>One method with its handler instances.</summary>
internal sealed record MethodHandlerMethod(
    string MethodId,
    MethodHandlerTransport Transport,
    MethodHandlerMethodState State,
    int LiveInstanceCount,
    DateTime? LastSeenUtc,
    IReadOnlyList<MethodHandlerInstance> Instances);

/// <summary>The unfiltered answer of the <c>MethodHandlers</c> query.</summary>
internal sealed record MethodHandlersView(MethodHandlersAnsweredBy AnsweredBy, IReadOnlyList<MethodHandlerMethod> Methods)
{
    /// <summary>Applies the <c>search</c> and <c>states</c> arguments.</summary>
    internal IReadOnlyList<MethodHandlerMethod> Filter(string? search, IReadOnlyCollection<MethodHandlerMethodState>? states)
    {
        IEnumerable<MethodHandlerMethod> result = Methods;
        if (states is { Count: > 0 })
        {
            result = result.Where(m => states.Contains(m.State));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            result = result.Where(m =>
                m.MethodId.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                m.Instances.Any(i => i.ServiceName.Contains(term, StringComparison.OrdinalIgnoreCase)));
        }

        return result.ToList();
    }
}

/// <summary>
///     Builds <see cref="MethodHandlersView" /> from the registry of this replica, the shared persisted snapshot and the
///     CK methods of the system tenant (AB#7837). The method list is the union of the registry entries, the in-process
///     methods and the system tenant's CK methods with a service handler that no instance announced. A missing or failing
///     snapshot store only leaves <see cref="MethodHandlerInstance.PersistedLastSeenUtc" /> empty; the exception is
///     logged and never returned.
/// </summary>
internal static class MethodHandlersViewBuilder
{
    internal static async Task<MethodHandlersView> BuildAsync(
        ICkMethodHandlerRegistryDiagnostics diagnostics,
        ICkMethodHandlerSnapshotStore? snapshotStore,
        ICkCacheService ckCacheService,
        string systemTenantId,
        string serviceVersion,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var data = diagnostics.GetDiagnostics();
        var persisted = await LoadPersistedAsync(snapshotStore, logger, cancellationToken);

        var entriesByMethod = data.Entries.ToLookup(e => e.MethodId, StringComparer.Ordinal);
        var inProcess = data.InProcessMethods.Select(m => m.MethodId).ToHashSet(StringComparer.Ordinal);
        var methodIds = new HashSet<string>(entriesByMethod.Select(g => g.Key), StringComparer.Ordinal);
        methodIds.UnionWith(inProcess);
        methodIds.UnionWith(ReadServiceHandlerMethodIds(ckCacheService, systemTenantId, logger));

        var methods = methodIds.OrderBy(m => m, StringComparer.Ordinal).Select(methodId =>
        {
            var instances = entriesByMethod[methodId].Select(e => new MethodHandlerInstance(
                e.ServiceName, e.InstanceId, e.Version, e.ContractVersion, e.Tenants, e.LastSeenUtc, e.ExpiresUtc,
                e.State, e.Source,
                persisted != null && persisted.TryGetValue((e.MethodId, e.InstanceId), out var lastSeen)
                    ? lastSeen
                    : null)).ToList();
            var live = instances.Count(i => i.State == CkMethodHandlerState.Live);
            var isInProcess = inProcess.Contains(methodId);
            var state = isInProcess || live > 0 ? MethodHandlerMethodState.Live
                : instances.Count > 0 ? MethodHandlerMethodState.NoLiveHandler
                : MethodHandlerMethodState.NotAnnounced;
            return new MethodHandlerMethod(methodId,
                isInProcess ? MethodHandlerTransport.InProcess : MethodHandlerTransport.Bus, state, live,
                instances.Count > 0 ? instances.Max(i => i.LastSeenUtc) : null, instances);
        }).ToList();

        var snapshot = data.Snapshot;
        return new MethodHandlersView(new MethodHandlersAnsweredBy(data.DispatcherInstanceId, serviceVersion,
            snapshot.Enabled, data.GeneratedAtUtc, snapshot.Load?.LoadedAtUtc, snapshot.Load?.RowsRestored,
            snapshot.Load?.Failed ?? false, snapshot.Flush.LastSuccessUtc, snapshot.Flush.LastFailed), methods);
    }

    private static async Task<Dictionary<(string MethodId, string InstanceId), DateTime>?> LoadPersistedAsync(
        ICkMethodHandlerSnapshotStore? store, ILogger logger, CancellationToken cancellationToken)
    {
        if (store == null)
        {
            return null;
        }

        try
        {
            var rows = await store.LoadAsync(cancellationToken);
            var result = new Dictionary<(string, string), DateTime>();
            foreach (var row in rows)
            {
                var key = (row.MethodId, row.InstanceId);
                if (!result.TryGetValue(key, out var existing) || row.LastSeenUtc > existing)
                {
                    result[key] = row.LastSeenUtc;
                }
            }

            return result;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The query still answers; the type name is enough in the log, the message may carry connection data.
            logger.LogWarning(e, "Reading the persisted method handler snapshot for the MethodHandlers query failed");
            return null;
        }
    }

    private static IEnumerable<string> ReadServiceHandlerMethodIds(ICkCacheService ckCacheService, string tenantId,
        ILogger logger)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (!ckCacheService.IsTenantLoaded(tenantId))
            {
                return result;
            }

            foreach (var type in ckCacheService.GetCkTypes(tenantId))
            {
                foreach (var method in type.AllMethods.Values)
                {
                    if (method.Handler == CkMethodHandlers.Service)
                    {
                        result.Add(method.QualifiedMethodId);
                    }
                }
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Reading the CK methods of the system tenant for the MethodHandlers query failed");
        }

        return result;
    }
}
