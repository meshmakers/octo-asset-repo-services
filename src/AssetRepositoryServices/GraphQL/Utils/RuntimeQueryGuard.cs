using System.Security.Claims;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Methods;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Observability;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;

/// <summary>The GraphQL entry points the unbounded-query guard watches (AB#8898).</summary>
internal enum RuntimeQueryEntryPoint
{
    /// <summary><c>runtime.runtimeEntities(ckId: …)</c>, the generic entity connection.</summary>
    GenericEntities,

    /// <summary><c>runtime.&lt;CkType&gt;</c>, the generated connection of one CK type.</summary>
    TypedEntities,

    /// <summary><c>runtime.runtimeQuery(rtId: …).items.rows</c>, the rows of a persisted query.</summary>
    PersistedQueryRows,

    /// <summary><c>runtime.transientQuery.*.items.rows</c>, the rows of a transient query.</summary>
    TransientQueryRows
}

/// <summary>Why a query is (not) unbounded.</summary>
internal enum RuntimeQueryBoundReason
{
    /// <summary>The selection has no items / edges / nodes (e.g. totalCount only): nothing is loaded per entity.</summary>
    NoItemsSelected,

    /// <summary>An aggregation or grouping aggregation query: the rows are aggregates, not entities.</summary>
    Aggregation,

    /// <summary><c>first</c> is at most <c>MaxPageSize</c>.</summary>
    PagedWithinLimit,

    /// <summary>A key lookup (<c>rtId</c> / <c>rtIds</c>) with at most <c>MaxPageSize</c> keys.</summary>
    KeyLookupWithinLimit,

    /// <summary>Unbounded: items are selected and there is no <c>first</c>.</summary>
    NoFirst,

    /// <summary>Unbounded: <c>first</c> is above <c>MaxPageSize</c>.</summary>
    FirstAboveLimit,

    /// <summary>Unbounded: a key lookup without a bounding <c>first</c> carries more keys than <c>MaxPageSize</c>.</summary>
    TooManyKeys
}

/// <summary>The facts of one query the classification decides on.</summary>
/// <param name="SelectsItems">The selection set selects <c>items</c>, <c>edges</c> or <c>nodes</c>.</param>
/// <param name="IsAggregation">The query is an aggregation or grouping aggregation query.</param>
/// <param name="First">The <c>first</c> argument, null when absent.</param>
/// <param name="KeyCount">The number of keys of a key lookup (<c>rtId</c> + <c>rtIds</c>), null when it is no key lookup.</param>
internal readonly record struct RuntimeQueryShape(bool SelectsItems, bool IsAggregation, int? First, int? KeyCount);

/// <summary>
///     Thrown (in <see cref="RuntimeQueryGuardMode.Enforce" />) when a query at the API boundary is unbounded. An
///     <see cref="ExecutionError" /> with the code <see cref="Statics.GraphQlUnboundedQueryRefused" /> (<c>ASSET1007</c>)
///     and <c>extensions.parameters</c> (<c>ckTypeId</c>, <c>maxPageSize</c>, <c>requestedFirst</c>, <c>keyCount</c>,
///     <c>reason</c>) plus <c>extensions.errorId</c>, the id of the warning log line.
/// </summary>
internal sealed class UnboundedRuntimeQueryException : ExecutionError
{
    /// <summary>Extension key of the error id.</summary>
    public const string ErrorIdKey = "errorId";

    public UnboundedRuntimeQueryException(string ckTypeId, int maxPageSize, int? requestedFirst, int? keyCount,
        RuntimeQueryBoundReason reason, string errorId)
        : base(CreateMessage(ckTypeId, maxPageSize, reason, errorId))
    {
        Code = Statics.GraphQlUnboundedQueryRefused;
        CkTypeId = ckTypeId;
        MaxPageSize = maxPageSize;
        ErrorId = errorId;
        Extensions = new Dictionary<string, object?>
        {
            [CkMethodGraphQlError.CodeKey] = Statics.GraphQlUnboundedQueryRefused,
            [ErrorIdKey] = errorId,
            [CkMethodGraphQlError.ParametersKey] = new Dictionary<string, object?>
            {
                ["ckTypeId"] = ckTypeId,
                ["maxPageSize"] = maxPageSize,
                ["requestedFirst"] = requestedFirst,
                ["keyCount"] = keyCount,
                ["reason"] = reason.ToString()
            }
        };
    }

    public string CkTypeId { get; }

    public int MaxPageSize { get; }

    public string ErrorId { get; }

    private static string CreateMessage(string ckTypeId, int maxPageSize, RuntimeQueryBoundReason reason,
        string errorId)
    {
        var cause = reason switch
        {
            RuntimeQueryBoundReason.FirstAboveLimit => $"'first' is above the limit of {maxPageSize}",
            RuntimeQueryBoundReason.TooManyKeys => $"the key lookup carries more than {maxPageSize} keys without 'first'",
            _ => "it selects items without 'first'"
        };
        return $"The query on CK type '{ckTypeId}' is unbounded and was refused: {cause}. " +
               $"Request at most {maxPageSize} items per page with 'first' (and 'after' for the next page), or select " +
               $"only 'totalCount' to count. Error id {errorId}.";
    }
}

/// <summary>
///     The unbounded runtime-entity query guard (AB#8898, Feature AB#8793 Slice 1): a query at the GraphQL API boundary
///     that selects items without <c>first</c>, or with <c>first</c> above
///     <see cref="RuntimeQueryGuardOptions.MaxPageSize" /> (and is no key lookup within that limit), is logged and counted
///     (<c>Warn</c>) or refused before the entities are read (<c>Enforce</c>), staged by
///     <see cref="RuntimeQueryGuardOptions.Mode" />.
/// </summary>
/// <remarks>
///     <para>
///         Called only from the resolvers the <see cref="RuntimeQueryGuardSchemaVisitor" /> wraps, so in-process engine
///         calls (<c>ITenantRepository</c>) never reach it. totalCount-only selections, aggregation queries and key
///         lookups with at most <c>MaxPageSize</c> keys are bounded (<see cref="Classify" />).
///     </para>
///     <para>
///         <c>Warn</c> logs one warning per unbounded query (tenant, CK type, requested size, client id, subject, operation
///         name, entry point); the log carries ids only, never a token or a user name.
///     </para>
/// </remarks>
internal sealed class RuntimeQueryGuard
{
    private static readonly string[] ItemFieldNames = ["items", "edges", "nodes"];

    private readonly IOptionsMonitor<RuntimeQueryGuardOptions> _options;
    private readonly RuntimeQueryGuardMetrics _metrics;
    private readonly ILogger<RuntimeQueryGuard> _logger;

    public RuntimeQueryGuard(IOptionsMonitor<RuntimeQueryGuardOptions> options, RuntimeQueryGuardMetrics metrics,
        ILogger<RuntimeQueryGuard> logger)
    {
        _options = options;
        _metrics = metrics;
        _logger = logger;
    }

    /// <summary>
    ///     THE classification of AB#8898 AC 2 (with the key-lookup rule of 2026-11-12): bounded unless items are selected
    ///     and neither <c>first</c> nor the key count is within <paramref name="maxPageSize" />.
    /// </summary>
    internal static RuntimeQueryBoundReason Classify(RuntimeQueryShape shape, int maxPageSize)
    {
        if (!shape.SelectsItems)
        {
            return RuntimeQueryBoundReason.NoItemsSelected;
        }

        if (shape.IsAggregation)
        {
            return RuntimeQueryBoundReason.Aggregation;
        }

        if (shape.First is { } first && first <= maxPageSize)
        {
            return RuntimeQueryBoundReason.PagedWithinLimit;
        }

        if (shape.KeyCount is { } keyCount)
        {
            return keyCount <= maxPageSize
                ? RuntimeQueryBoundReason.KeyLookupWithinLimit
                : RuntimeQueryBoundReason.TooManyKeys;
        }

        return shape.First == null ? RuntimeQueryBoundReason.NoFirst : RuntimeQueryBoundReason.FirstAboveLimit;
    }

    /// <summary>True when <paramref name="reason" /> means unbounded.</summary>
    internal static bool IsUnbounded(RuntimeQueryBoundReason reason) =>
        reason is RuntimeQueryBoundReason.NoFirst or RuntimeQueryBoundReason.FirstAboveLimit
            or RuntimeQueryBoundReason.TooManyKeys;

    /// <summary>
    ///     Checks the query of <paramref name="context" />; throws <see cref="UnboundedRuntimeQueryException" /> when it is
    ///     unbounded and the mode is <c>Enforce</c>.
    /// </summary>
    internal void Check(IResolveFieldContext context, RuntimeQueryEntryPoint entryPoint)
    {
        var options = _options.CurrentValue;
        if (options.Mode == RuntimeQueryGuardMode.Off)
        {
            return;
        }

        var maxPageSize = options.EffectiveMaxPageSize;
        var shape = GetShape(context, entryPoint);
        var reason = Classify(shape, maxPageSize);
        if (!IsUnbounded(reason))
        {
            return;
        }

        var userContext = context.UserContext as GraphQlUserContext;
        var tenantId = userContext?.TenantId ?? string.Empty;
        var user = userContext?.User ?? context.User;
        var clientId = user?.FindFirst("client_id")?.Value;
        var subject = user?.FindFirst("sub")?.Value ?? user?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var ckTypeId = GetCkTypeId(context, entryPoint);
        var mode = options.Mode == RuntimeQueryGuardMode.Enforce ? "enforce" : "warn";
        var errorId = Guid.NewGuid().ToString("N");

        _metrics.RecordUnbounded(tenantId, GetMetricCkTypeId(context, entryPoint, tenantId, ckTypeId), clientId, mode);

        // This line is the caller inventory the switch to Enforce is decided on (AB#8899).
        _logger.LogWarning(
            "Unbounded runtime-entity query ({Reason}) on CK type {CkTypeId} in tenant {TenantId}: requested first {RequestedFirst}, keys {KeyCount}, limit {MaxPageSize}, entry point {EntryPoint}, operation {OperationName}, client {ClientId}, subject {Subject}, mode {Mode}, error id {ErrorId} (AB#8898)",
            reason, ckTypeId, tenantId, shape.First, shape.KeyCount, maxPageSize, entryPoint,
            context.Operation?.Name?.StringValue, clientId, subject, mode, errorId);

        if (options.Mode == RuntimeQueryGuardMode.Enforce)
        {
            throw new UnboundedRuntimeQueryException(ckTypeId, maxPageSize, shape.First, shape.KeyCount, reason, errorId);
        }
    }

    internal static RuntimeQueryShape GetShape(IResolveFieldContext context, RuntimeQueryEntryPoint entryPoint)
    {
        // The keys of SubFields are response keys (aliases); the field name is on the AST node. Fragments are merged.
        var selectsItems = context.SubFields?.Values.Any(f => ItemFieldNames.Contains(f.Field.Name.StringValue)) == true;
        var first = context.GetArgument<int?>("first");

        int? keyCount = null;
        bool isAggregation = false;
        switch (entryPoint)
        {
            case RuntimeQueryEntryPoint.GenericEntities:
            case RuntimeQueryEntryPoint.TypedEntities:
                keyCount = GetKeyCount(context);
                break;
            case RuntimeQueryEntryPoint.PersistedQueryRows:
                isAggregation = context.Source is RtQueryDto persisted && IsAggregation(persisted.Columns);
                break;
            case RuntimeQueryEntryPoint.TransientQueryRows:
                isAggregation = context.Source is RtTransientQueryDto transient && IsAggregation(transient.Columns);
                break;
        }

        return new RuntimeQueryShape(selectsItems, isAggregation, first, keyCount);
    }

    // The entity resolvers load by key when rtId or rtIds is given (an empty rtIds returns nothing).
    private static int? GetKeyCount(IResolveFieldContext context)
    {
        var hasRtId = context.HasArgument(Statics.RtIdArg);
        var hasRtIds = context.HasArgument(Statics.RtIdsArg);
        if (!hasRtId && !hasRtIds)
        {
            return null;
        }

        var count = 0;
        if (hasRtId && context.GetArgument<OctoObjectId?>(Statics.RtIdArg) != null)
        {
            count++;
        }

        if (hasRtIds)
        {
            count += context.GetArgument<IEnumerable<OctoObjectId>?>(Statics.RtIdsArg)?.Count() ?? 0;
        }

        return count;
    }

    // A persisted or transient aggregation / grouping aggregation query has at least one aggregated column.
    private static bool IsAggregation(IEnumerable<RtQueryColumnDto>? columns) =>
        columns?.Any(c => c.AggregationType != AggregationTypesDto.None) == true;

    private static string GetCkTypeId(IResolveFieldContext context, RuntimeQueryEntryPoint entryPoint)
    {
        var ckTypeId = entryPoint switch
        {
            RuntimeQueryEntryPoint.TypedEntities =>
                context.FieldDefinition.Metadata.TryGetValue(Statics.CkId, out var value) ? value?.ToString() : null,
            RuntimeQueryEntryPoint.GenericEntities => NormalizeCkTypeId(context.GetArgument<string?>(Statics.CkIdArg)),
            RuntimeQueryEntryPoint.PersistedQueryRows => (context.Source as RtQueryDto)?.AssociatedCkTypeId?.ToString(),
            RuntimeQueryEntryPoint.TransientQueryRows =>
                (context.Source as RtTransientQueryDto)?.AssociatedCkTypeId?.ToString(),
            _ => null
        };
        return string.IsNullOrEmpty(ckTypeId) ? "unknown" : ckTypeId;
    }

    // The ck_type label must stay bounded: the generic entry point takes the CK type id from the caller, so only a type
    // that exists in the tenant's CK cache becomes a series; anything else is "unknown" (the raw value is in the log).
    // The other entry points take the id from the schema or the stored query.
    private static string GetMetricCkTypeId(IResolveFieldContext context, RuntimeQueryEntryPoint entryPoint,
        string tenantId, string ckTypeId)
    {
        if (entryPoint != RuntimeQueryEntryPoint.GenericEntities)
        {
            return ckTypeId;
        }

        return IsKnownCkType(context.RequestServices?.GetService<ICkCacheService>(), tenantId, ckTypeId)
            ? ckTypeId
            : RuntimeQueryGuardMetrics.UnknownCkType;
    }

    internal static bool IsKnownCkType(ICkCacheService? ckCacheService, string tenantId, string ckTypeId)
    {
        if (ckCacheService == null)
        {
            return false;
        }

        try
        {
            return ckCacheService.GetRtCkType(tenantId, new RtCkId<CkTypeId>(ckTypeId)) != null;
        }
        catch (Exception e) when (e is CkCacheException or KeyNotFoundException or ArgumentException or FormatException)
        {
            return false;
        }
    }

    // The generic entry point takes the CK type id as a string; write it in the same form as the typed entry points
    // (RtCkId), so one CK type is one metric series. A malformed id is kept as given (the resolver refuses it).
    private static string? NormalizeCkTypeId(string? ckTypeId)
    {
        if (string.IsNullOrEmpty(ckTypeId))
        {
            return ckTypeId;
        }

        try
        {
            return new RtCkId<CkTypeId>(ckTypeId).ToString();
        }
        catch (Exception e) when (e is ArgumentException or FormatException)
        {
            return ckTypeId;
        }
    }
}
