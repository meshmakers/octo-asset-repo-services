using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Methods;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Observability;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
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
    TransientQueryRows,

    /// <summary>
    ///     A typed navigation field inside an entity (<c>children</c>, <c>parent</c>, …), one per association
    ///     navigation name of the CK type.
    /// </summary>
    NestedTypedNavigation,

    /// <summary>The navigation <c>associations(roleId, direction, ckId)</c> inside a typed entity.</summary>
    NestedGenericAssociations,

    /// <summary>The navigation <c>associations { targets(roleId, direction, ckId) }</c> inside a generic entity.</summary>
    NestedGenericTargets,

    /// <summary>The navigation <c>associations { definitions(direction, roleId?) }</c> (association records) inside a generic entity.</summary>
    NestedGenericDefinitions
}

/// <summary>
///     The guard's decision on one unbounded nested navigation of a request, kept per response path so the
///     navigation is logged and counted once, however many parent entities resolve it.
/// </summary>
internal sealed record NestedNavigationGuardDecision(string CkTypeId, string FieldPath, RuntimeQueryBoundReason Reason,
    int? RequestedFirst, int? KeyCount, int MaxPageSize, bool Enforce, string ErrorId);

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

    /// <param name="ckTypeId">The CK type the query (or the navigation) reads.</param>
    /// <param name="maxPageSize">The configured limit.</param>
    /// <param name="requestedFirst">The <c>first</c> argument, null when absent.</param>
    /// <param name="keyCount">The keys of a key lookup, null when it is none.</param>
    /// <param name="reason">Why the query is unbounded.</param>
    /// <param name="errorId">The id of the warning log line.</param>
    /// <param name="fieldPath">
    ///     The path of a nested to-many navigation (field names, no list indices), null for a top-level query.
    ///     When set, the message names it and <c>extensions.parameters.fieldPath</c> carries it.
    /// </param>
    public UnboundedRuntimeQueryException(string ckTypeId, int maxPageSize, int? requestedFirst, int? keyCount,
        RuntimeQueryBoundReason reason, string errorId, string? fieldPath = null)
        : base(CreateMessage(ckTypeId, maxPageSize, reason, errorId, fieldPath))
    {
        Code = Statics.GraphQlUnboundedQueryRefused;
        CkTypeId = ckTypeId;
        MaxPageSize = maxPageSize;
        ErrorId = errorId;
        FieldPath = fieldPath;
        var parameters = new Dictionary<string, object?>
        {
            ["ckTypeId"] = ckTypeId,
            ["maxPageSize"] = maxPageSize,
            ["requestedFirst"] = requestedFirst,
            ["keyCount"] = keyCount,
            ["reason"] = reason.ToString()
        };
        if (fieldPath != null)
        {
            parameters["fieldPath"] = fieldPath;
        }

        Extensions = new Dictionary<string, object?>
        {
            [CkMethodGraphQlError.CodeKey] = Statics.GraphQlUnboundedQueryRefused,
            [ErrorIdKey] = errorId,
            [CkMethodGraphQlError.ParametersKey] = parameters
        };
    }

    public string CkTypeId { get; }

    public int MaxPageSize { get; }

    public string ErrorId { get; }

    /// <summary>The path of the refused nested navigation, null for a top-level query.</summary>
    public string? FieldPath { get; }

    private static string CreateMessage(string ckTypeId, int maxPageSize, RuntimeQueryBoundReason reason,
        string errorId, string? fieldPath)
    {
        var cause = reason switch
        {
            RuntimeQueryBoundReason.FirstAboveLimit => $"'first' is above the limit of {maxPageSize}",
            RuntimeQueryBoundReason.TooManyKeys => $"the key lookup carries more than {maxPageSize} keys without 'first'",
            _ => "it selects items without 'first'"
        };
        var subject = fieldPath == null
            ? $"The query on CK type '{ckTypeId}'"
            : $"The to-many navigation '{fieldPath}' (CK type '{ckTypeId}')";
        return $"{subject} is unbounded and was refused: {cause}. " +
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
        if (IsNested(entryPoint))
        {
            CheckNested(context, entryPoint, options.Mode, maxPageSize);
            return;
        }

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

    /// <summary>True for the navigation connections inside an entity.</summary>
    internal static bool IsNested(RuntimeQueryEntryPoint entryPoint) =>
        entryPoint is RuntimeQueryEntryPoint.NestedTypedNavigation or RuntimeQueryEntryPoint.NestedGenericAssociations
            or RuntimeQueryEntryPoint.NestedGenericTargets or RuntimeQueryEntryPoint.NestedGenericDefinitions;

    // A nested navigation is checked once per parent entity; the decision is taken (logged, counted) once per
    // response path of the request and repeated for the other parents. In Enforce every parent gets the refusal, with
    // the same error id, before its load is queued.
    private void CheckNested(IResolveFieldContext context, RuntimeQueryEntryPoint entryPoint, RuntimeQueryGuardMode mode,
        int maxPageSize)
    {
        var userContext = context.UserContext as GraphQlUserContext;
        var tenantId = userContext?.TenantId ?? string.Empty;
        if (IsToOneNavigation(context, entryPoint, tenantId))
        {
            return;
        }

        var shape = GetShape(context, entryPoint);
        var reason = Classify(shape, maxPageSize);
        if (!IsUnbounded(reason))
        {
            return;
        }

        var enforce = mode == RuntimeQueryGuardMode.Enforce;
        var candidate = new NestedNavigationGuardDecision(GetCkTypeId(context, entryPoint), GetFieldPath(context.Path),
            reason, shape.First, shape.KeyCount, maxPageSize, enforce, Guid.NewGuid().ToString("N"));
        var decision = candidate;
        var isFirst = true;
        if (userContext != null)
        {
            // A user context can outlive one operation (a subscription connection, a batched HTTP request), and one
            // response path can carry several type-specific variants of a navigation (fragments on the members of a
            // union or interface). So the key holds the operation, the mode and every fact of the decision: a reused
            // decision is one with exactly these facts, taken in this operation and this mode.
            var key = string.Join('|', entryPoint, OperationIdentity(context), mode, GetFieldPath(context.ResponsePath),
                candidate.CkTypeId, reason, shape.First?.ToString(CultureInfo.InvariantCulture) ?? "-",
                shape.KeyCount?.ToString(CultureInfo.InvariantCulture) ?? "-", maxPageSize);
            isFirst = userContext.NestedNavigationGuardDecisions.TryAdd(key, candidate);
            if (!isFirst)
            {
                decision = userContext.NestedNavigationGuardDecisions[key];
            }
        }

        if (isFirst)
        {
            var user = userContext?.User ?? context.User;
            var clientId = user?.FindFirst("client_id")?.Value;
            var subject = user?.FindFirst("sub")?.Value ?? user?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var modeLabel = decision.Enforce ? "enforce" : "warn";

            _metrics.RecordUnbounded(tenantId, GetMetricCkTypeId(context, entryPoint, tenantId, decision.CkTypeId),
                clientId, modeLabel);

            // The caller inventory of nested navigations.
            _logger.LogWarning(
                "Unbounded nested navigation ({Reason}) at {FieldPath} on CK type {CkTypeId} in tenant {TenantId}: requested first {RequestedFirst}, keys {KeyCount}, limit {MaxPageSize}, entry point {EntryPoint}, operation {OperationName}, client {ClientId}, subject {Subject}, mode {Mode}, error id {ErrorId}",
                decision.Reason, decision.FieldPath, decision.CkTypeId, tenantId, decision.RequestedFirst,
                decision.KeyCount, decision.MaxPageSize, entryPoint, context.Operation?.Name?.StringValue, clientId,
                subject, modeLabel, decision.ErrorId);
        }

        if (enforce)
        {
            // A new error per parent: the executor writes the path and location into the error it catches.
            throw new UnboundedRuntimeQueryException(decision.CkTypeId, decision.MaxPageSize, decision.RequestedFirst,
                decision.KeyCount, decision.Reason, decision.ErrorId, decision.FieldPath);
        }
    }

    /// <summary>
    ///     True only when the navigation provably returns at most one entity per parent. A typed navigation
    ///     carries it as metadata (every association behind the name One / ZeroOrOne); a generic one is looked up by
    ///     role and direction in the tenant's CK cache. Indirect traversal, a missing role, an unknown role or direction
    ///     and every failure count as to-many (fail closed).
    /// </summary>
    internal static bool IsToOneNavigation(IResolveFieldContext context, RuntimeQueryEntryPoint entryPoint,
        string tenantId)
    {
        switch (entryPoint)
        {
            case RuntimeQueryEntryPoint.NestedTypedNavigation:
                return context.FieldDefinition.Metadata.TryGetValue(Statics.NavigationToOne, out var toOne) &&
                       toOne is true;
            case RuntimeQueryEntryPoint.NestedGenericAssociations:
            case RuntimeQueryEntryPoint.NestedGenericTargets:
            case RuntimeQueryEntryPoint.NestedGenericDefinitions:
                if (RawArgument(context, Statics.IncludeIndirectArg) is not (null or false))
                {
                    return false;
                }

                var roleId = RawArgument(context, Statics.RoleIdArg) as string;
                if (string.IsNullOrWhiteSpace(roleId) || RawArgument(context, Statics.DirectionArg) is not GraphDirections
                        direction)
                {
                    return false;
                }

                var ckCacheService = context.RequestServices?.GetService<ICkCacheService>();
                if (ckCacheService == null)
                {
                    return false;
                }

                try
                {
                    var role = ckCacheService.GetRtCkAssociationRole(tenantId,
                        new RtCkId<CkAssociationRoleId>(roleId));
                    var multiplicity = direction switch
                    {
                        GraphDirections.Outbound => role.OutboundMultiplicity,
                        GraphDirections.Inbound => role.InboundMultiplicity,
                        _ => MultiplicitiesDto.N
                    };
                    return multiplicity is MultiplicitiesDto.One or MultiplicitiesDto.ZeroOrOne;
                }
                catch (Exception)
                {
                    // Unknown or malformed role: not provably to-one. The resolver reports the error itself.
                    return false;
                }
            default:
                return false;
        }
    }

    /// <summary>
    ///     The page size the connection resolvers really use: GraphQL.NET's connection context hands them the absolute
    ///     value of <c>first</c>, so <c>first: -5000</c> loads 5000 items. Classifying the raw argument would let a negative
    ///     <c>first</c> pass as within the limit. <see cref="int.MinValue" /> has no positive counterpart and maps to
    ///     <see cref="int.MaxValue" />.
    /// </summary>
    internal static int? EffectivePageSize(int? first) => first switch
    {
        null => null,
        int.MinValue => int.MaxValue,
        < 0 => -first.Value,
        _ => first
    };

    // The operation being executed: the parsed document and its operation definition are created per execution, so
    // their identity tells two operations on one user context apart.
    private static string OperationIdentity(IResolveFieldContext context) =>
        string.Join(':', RuntimeHelpers.GetHashCode(context.Document), RuntimeHelpers.GetHashCode(context.Operation));

    // The value of an argument as the executor coerced it (the enum value for an enum argument), null when absent.
    private static object? RawArgument(IResolveFieldContext context, string name) =>
        context.Arguments != null && context.Arguments.TryGetValue(name, out var value) ? value.Value : null;

    // A path for logs, the error and the per-request decision: the names of the fields, list indices left out.
    internal static string GetFieldPath(IEnumerable<object>? path) =>
        path == null ? string.Empty : string.Join('.', path.OfType<string>());

    internal static RuntimeQueryShape GetShape(IResolveFieldContext context, RuntimeQueryEntryPoint entryPoint)
    {
        // The keys of SubFields are response keys (aliases); the field name is on the AST node. Fragments are merged.
        var selectsItems = context.SubFields?.Values.Any(f => ItemFieldNames.Contains(f.Field.Name.StringValue)) == true;
        var first = EffectivePageSize(context.GetArgument<int?>("first"));

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
            case RuntimeQueryEntryPoint.NestedTypedNavigation:
                keyCount = GetNestedKeyCount(context);
                break;
            case RuntimeQueryEntryPoint.NestedGenericDefinitions:
                // relatedRtId narrows the association records to those with one related entity.
                keyCount = RawArgument(context, Statics.RelatedRtId) != null ? 1 : null;
                break;
        }

        return new RuntimeQueryShape(selectsItems, isAggregation, first, keyCount);
    }

    // Unlike the top-level resolvers (which answer an empty page for rtId: null / rtIds: []), the navigation
    // resolver filters by keys only when it got at least one: rtId: null, rtIds: null or rtIds: [] load every target.
    // So only the keys that are really there count, and none is no key lookup at all.
    private static int? GetNestedKeyCount(IResolveFieldContext context)
    {
        var count = RawArgument(context, Statics.RtIdArg) != null ? 1 : 0;
        if (RawArgument(context, Statics.RtIdsArg) is System.Collections.IEnumerable keys)
        {
            count += keys.Cast<object?>().Count(k => k != null);
        }

        return count > 0 ? count : null;
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
            // The target type of a typed navigation (the base type when it is abstract), from the schema.
            // (RtEntityAssociationType carries its target type as CkId).
            RuntimeQueryEntryPoint.NestedTypedNavigation =>
                context.FieldDefinition.Metadata.TryGetValue(Statics.QueryBaseType, out var baseType)
                    ? baseType?.ToString()
                    : context.FieldDefinition.Metadata.TryGetValue(Statics.CkId, out var targetType)
                        ? targetType?.ToString()
                        : null,
            RuntimeQueryEntryPoint.NestedGenericAssociations or RuntimeQueryEntryPoint.NestedGenericTargets =>
                NormalizeCkTypeId(RawArgument(context, Statics.CkIdArg) as string),
            // Association records have no target type: the type of the entity they belong to.
            RuntimeQueryEntryPoint.NestedGenericDefinitions =>
                (context.Source as RtEntityGenericAssociation)?.RtEntityDto.CkTypeId?.ToString(),
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
        if (entryPoint is not (RuntimeQueryEntryPoint.GenericEntities or RuntimeQueryEntryPoint.NestedGenericAssociations
            or RuntimeQueryEntryPoint.NestedGenericTargets))
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
        catch (Exception e) when (e is CkCacheException or KeyNotFoundException || IsMalformedCkTypeId(e))
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
        catch (Exception e) when (IsMalformedCkTypeId(e))
        {
            return ckTypeId;
        }
    }

    // A malformed id (e.g. "Model/Type-bad") fails in the CkTypeId constructor, which RtCkId creates by reflection:
    // the ArgumentException arrives wrapped in a TargetInvocationException.
    private static bool IsMalformedCkTypeId(Exception e) =>
        e is ArgumentException or FormatException ||
        e is TargetInvocationException { InnerException: ArgumentException or FormatException };
}
