using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;

/// <summary>
///     CK v2 (AB#5668): keeps attributes with <c>access: Hidden</c> out of every runtime query path (columns, filters,
///     sort, search, aggregations, group-by, entity selectors) and keeps <c>Hidden</c> / <c>MethodOnly</c> attributes
///     out of query-row writes. The engine's query column collector and the repository do not know about access, so
///     the asset-repo guards every entry point.
///     <para>
///         Plain and record paths (<c>passwordHash</c>, <c>address.street</c>) are resolved exactly against the query
///         type (and, for association targets, every type derived from it). Paths that cross a navigation
///         (<c>-&gt;</c>), entity-selector keys (<c>nav.type[passwordHash=X]</c>) and paths against a type the CK cache
///         does not know are checked by attribute name against every hidden assignment of the tenant (conservative,
///         fail closed).
///     </para>
///     <para>
///         The set of hidden attribute names is computed once per loaded CK model graph: it is cached against the
///         type collection instance of the tenant's CK cache, which is replaced whenever the cache is reloaded.
///     </para>
/// </summary>
internal static partial class AccessQueryGuard
{
    internal const string QueryColumnOperation = "query column";
    internal const string QueryRowWriteOperation = "query row update";
    internal const string AssociationQueryOperation = "association query";

    private static readonly ConditionalWeakTable<object, HiddenNameSet> HiddenNameCache = new();

    [GeneratedRegex(@"\[([^\[\]=]+)=[^\[\]]*\]", RegexOptions.Compiled)]
    private static partial Regex EntitySelectorKeyRegex();

    internal static bool IsHidden(CkTypeAttributeGraph? attribute)
    {
        return attribute != null && !AttributeAccess.IsExposedInOutput(attribute.Access);
    }

    /// <summary>
    ///     True when the attribute is not writable through the generic surface (<c>Hidden</c> or <c>MethodOnly</c>).
    /// </summary>
    internal static bool IsNotGenericallyWritable(CkTypeAttributeGraph? attribute)
    {
        return attribute != null && !AttributeAccess.IsExposedInGenericInput(attribute.Access);
    }

    /// <summary>
    ///     True when the attribute path ends on a hidden attribute or carries a hidden entity-selector key.
    /// </summary>
    internal static bool IsHiddenPath(ICkCacheService ckCacheService, string tenantId,
        CkTypeWithAttributesGraph? root, string? attributePath)
    {
        return IsHiddenPath(ckCacheService, tenantId, root, attributePath, GetHiddenAttributeNames(ckCacheService, tenantId));
    }

    private static bool IsHiddenPath(ICkCacheService ckCacheService, string tenantId,
        CkTypeWithAttributesGraph? root, string? attributePath, IReadOnlySet<string> hiddenNames)
    {
        if (string.IsNullOrWhiteSpace(attributePath) || hiddenNames.Count == 0)
        {
            return false;
        }

        // L10: an entity selector ([passwordHash=X]) is an equality lookup on the selector key.
        foreach (Match match in EntitySelectorKeyRegex().Matches(attributePath))
        {
            if (hiddenNames.Contains(LastName(match.Groups[1].Value)))
            {
                return true;
            }
        }

        var normalized = QueryColumnPathResolver.NormalizePath(attributePath);
        if (!normalized.Contains("->") && root != null)
        {
            return IsHidden(SecretQueryGuard.TryResolveAttribute(ckCacheService, tenantId, root, normalized));
        }

        // Navigation paths and unknown roots: by name (fail closed).
        var lastSegment = normalized.Contains("->")
            ? normalized[(normalized.LastIndexOf("->", StringComparison.Ordinal) + 2)..]
            : normalized;
        if (lastSegment.Contains("::"))
        {
            return false; // association meta column (totalCount / exists)
        }

        return hiddenNames.Contains(LastName(lastSegment));
    }

    /// <summary>
    ///     Removes the columns whose path ends on a hidden attribute.
    /// </summary>
    internal static IReadOnlyCollection<CkTypeQueryColumn> WithoutHiddenColumns(ICkCacheService ckCacheService,
        string tenantId, RtCkId<CkTypeId> ckTypeId, IReadOnlyCollection<CkTypeQueryColumn> columns)
    {
        var hiddenNames = GetHiddenAttributeNames(ckCacheService, tenantId);
        if (hiddenNames.Count == 0)
        {
            return columns;
        }

        var root = TryGetType(ckCacheService, tenantId, ckTypeId);
        return columns.Where(c => !IsHiddenPath(ckCacheService, tenantId, root, c.Path, hiddenNames)).ToList();
    }

    /// <summary>
    ///     Throws <see cref="HiddenAttributeAccessException" /> for the first path that ends on a hidden attribute.
    /// </summary>
    internal static void EnsureNoHiddenColumns(ICkCacheService ckCacheService, string tenantId,
        RtCkId<CkTypeId> ckTypeId, IEnumerable<string> paths, string operation)
    {
        var hiddenNames = GetHiddenAttributeNames(ckCacheService, tenantId);
        if (hiddenNames.Count == 0)
        {
            return;
        }

        var root = TryGetType(ckCacheService, tenantId, ckTypeId);
        foreach (var path in paths)
        {
            if (IsHiddenPath(ckCacheService, tenantId, root, path, hiddenNames))
            {
                throw HiddenAttributeAccessException.NotQueryable(path, ckTypeId.ToString(), operation);
            }
        }
    }

    /// <summary>
    ///     M7: a query-row write must not set a <c>Hidden</c> or <c>MethodOnly</c> attribute (same rule as the generic
    ///     mutations). Throws <see cref="HiddenAttributeAccessException" /> (<c>ATTRIBUTE_NOT_WRITABLE</c>).
    /// </summary>
    internal static void EnsureWritablePath(ICkCacheService ckCacheService, string tenantId,
        RtCkId<CkTypeId> ckTypeId, string attributePath)
    {
        var root = TryGetType(ckCacheService, tenantId, ckTypeId);
        var normalized = QueryColumnPathResolver.NormalizePath(attributePath);
        if (root != null && !normalized.Contains("->"))
        {
            var attribute = SecretQueryGuard.TryResolveAttribute(ckCacheService, tenantId, root, normalized);
            if (IsNotGenericallyWritable(attribute))
            {
                throw HiddenAttributeAccessException.NotWritable(attributePath, ckTypeId.ToString(), attribute!.Access);
            }
        }

        if (IsHiddenPath(ckCacheService, tenantId, root, attributePath))
        {
            throw HiddenAttributeAccessException.NotWritable(attributePath, ckTypeId.ToString(),
                CkAttributeAccessDto.Hidden);
        }
    }

    /// <summary>
    ///     Throws <see cref="HiddenAttributeAccessException" /> when the path ends on a hidden attribute (used by
    ///     <see cref="SecretQueryGuard" /> for filters, sort, search, aggregations and group-by).
    /// </summary>
    internal static void EnsureNotHidden(ICkCacheService ckCacheService, string tenantId,
        CkTypeWithAttributesGraph root, string entityName, string? attributePath, string operation)
    {
        if (attributePath != null && IsHiddenPath(ckCacheService, tenantId, root, attributePath))
        {
            throw HiddenAttributeAccessException.NotQueryable(attributePath, entityName, operation);
        }
    }

    /// <summary>
    ///     H1: validates the query options of an association / navigation connection before they reach the repository.
    ///     Every filter (incl. nested), sort, attribute-search, aggregation and group-by path is checked against the
    ///     target type and every type derived from it (polymorphic targets); an unknown target is checked by name.
    ///     Secret rules are enforced by the repository itself (AB#5533) and are not repeated here.
    /// </summary>
    internal static void EnsureQueryOptionsAllowed(ICkCacheService ckCacheService, string tenantId,
        RtCkId<CkTypeId>? targetCkTypeId, RtEntityQueryOptions queryOptions)
    {
        var hiddenNames = GetHiddenAttributeNames(ckCacheService, tenantId);
        if (hiddenNames.Count == 0)
        {
            return;
        }

        var paths = CollectPaths(queryOptions).ToList();
        if (paths.Count == 0)
        {
            return;
        }

        var target = targetCkTypeId == null ? null : TryGetType(ckCacheService, tenantId, targetCkTypeId);
        var roots = new List<CkTypeWithAttributesGraph?>();
        if (target == null)
        {
            roots.Add(null); // by name, fail closed
        }
        else
        {
            roots.Add(target);
            foreach (var derived in target.GetAllDerivedTypes(false))
            {
                roots.Add(ckCacheService.TryGetCkType(tenantId, derived, out var derivedGraph) ? derivedGraph : null);
            }
        }

        var entityName = targetCkTypeId?.ToString() ?? "<unknown>";
        foreach (var (path, operation) in paths)
        {
            if (roots.Any(root => IsHiddenPath(ckCacheService, tenantId, root, path, hiddenNames)))
            {
                throw HiddenAttributeAccessException.NotQueryable(path, entityName, operation);
            }
        }
    }

    /// <summary>
    ///     L11: true when a stored attribute name (any casing) is assigned as Hidden anywhere in the tenant. Used where
    ///     attributes are projected without a CK type (unknown type in the cache) — fail closed.
    /// </summary>
    internal static bool IsHiddenName(ICkCacheService ckCacheService, string tenantId, string attributeName)
    {
        return GetHiddenAttributeNames(ckCacheService, tenantId).Contains(attributeName.ToPascalCase());
    }

    /// <summary>
    ///     All hidden attribute names (PascalCase) of the tenant's types and records. Cached per loaded CK model
    ///     graph (M8). Fail closed: a tenant that is not loaded throws instead of reporting "nothing hidden".
    /// </summary>
    internal static IReadOnlySet<string> GetHiddenAttributeNames(ICkCacheService ckCacheService, string tenantId)
    {
        var types = ckCacheService.GetCkTypes(tenantId); // throws CkCacheException when the tenant is not loaded
        return HiddenNameCache.GetValue(types, _ => new HiddenNameSet(Compute(ckCacheService, tenantId, types))).Names;
    }

    private static IReadOnlySet<string> Compute(ICkCacheService ckCacheService, string tenantId,
        IEnumerable<CkTypeGraph> types)
    {
        var attributes = types.SelectMany(t => t.AllAttributes.Values)
            .Concat(ckCacheService.GetCkRecords(tenantId).SelectMany(r => r.AllAttributes.Values));
        return attributes.Where(IsHidden).Select(a => a.AttributeName).ToHashSet(StringComparer.Ordinal);
    }

    private static IEnumerable<(string Path, string Operation)> CollectPaths(RtEntityQueryOptions queryOptions)
    {
        foreach (var path in CollectFilterPaths(queryOptions))
        {
            yield return path;
        }

        foreach (var sortOrder in queryOptions.SortOrders ?? [])
        {
            yield return (sortOrder.AttributePath, SecretQueryGuard.SortOperation);
        }

        foreach (var path in queryOptions.AttributeSearchFilter?.AttributePaths ?? [])
        {
            yield return (path, SecretQueryGuard.AttributeSearchOperation);
        }

        if (queryOptions.ResultAggregation != null)
        {
            foreach (var path in AggregationPaths(queryOptions.ResultAggregation))
            {
                yield return (path, SecretQueryGuard.AggregationOperation);
            }
        }

        if (queryOptions.FieldAggregation != null)
        {
            foreach (var path in queryOptions.FieldAggregation.GroupByAttributePathList)
            {
                yield return (path, SecretQueryGuard.GroupByOperation);
            }

            foreach (var path in AggregationPaths(queryOptions.FieldAggregation))
            {
                yield return (path, SecretQueryGuard.AggregationOperation);
            }
        }
    }

    private static IEnumerable<(string Path, string Operation)> CollectFilterPaths(FieldFilterCriteria criteria)
    {
        foreach (var fieldFilter in criteria.FieldFilters ?? [])
        {
            yield return (fieldFilter.AttributePath, $"filter operator '{fieldFilter.Operator}'");
        }

        foreach (var nested in criteria.NestedFilters ?? [])
        {
            foreach (var path in CollectFilterPaths(nested))
            {
                yield return path;
            }
        }
    }

    private static IEnumerable<string> AggregationPaths(AggregationInput aggregationInput)
    {
        return aggregationInput.CountAttributePathList
            .Concat(aggregationInput.MinValueAttributePathList)
            .Concat(aggregationInput.MaxValueAttributePathList)
            .Concat(aggregationInput.AvgAttributePathList)
            .Concat(aggregationInput.SumAttributePathList);
    }

    private static CkTypeGraph? TryGetType(ICkCacheService ckCacheService, string tenantId, RtCkId<CkTypeId> ckTypeId)
    {
        return ckCacheService.TryGetRtCkType(tenantId, ckTypeId, out var ckTypeGraph) ? ckTypeGraph : null;
    }

    private static string LastName(string path)
    {
        var name = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? string.Empty;
        return name.Split('[')[0].ToPascalCase();
    }

    private sealed class HiddenNameSet(IReadOnlySet<string> names)
    {
        public IReadOnlySet<string> Names { get; } = names;
    }
}
