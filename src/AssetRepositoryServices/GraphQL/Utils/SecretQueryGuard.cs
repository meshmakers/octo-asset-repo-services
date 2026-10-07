using System.Text.RegularExpressions;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.Secrets;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;

/// <summary>
///     Early GraphQL-side validation of query arguments against <c>Secret</c> attributes (AB#5528, concept §4.4):
///     a secret can only be filtered with <c>IS_NULL</c> / <c>IS_NOT_NULL</c>; sort, attribute search,
///     aggregations and group-by on a secret are refused with <see cref="SecretAttributeNotQueryableException" />
///     (GraphQL error code <c>SecretAttributeNotQueryable</c>). The repository enforces the same rules
///     (AB#5533); this guard only fails before any database round trip and covers paths the repository does
///     not see as a query (change-stream filters).
/// </summary>
/// <remarks>
///     Paths are resolved through the CK type and record graph (dotted record paths, array indexes ignored).
///     Navigation paths (<c>-&gt;</c>, <c>::</c>) are left to the repository. A path that does not resolve is
///     not this guard's business either - the repository reports unknown paths.
/// </remarks>
internal static partial class SecretQueryGuard
{
    internal const string SortOperation = "sort";
    internal const string AttributeSearchOperation = "attribute search";
    internal const string AggregationOperation = "aggregation";
    internal const string GroupByOperation = "group-by";

    [GeneratedRegex(@"\[[^\]]*\]", RegexOptions.Compiled)]
    private static partial Regex IndexerRegex();

    /// <summary>
    ///     Validates all attribute paths of the query options of a query on <paramref name="ckTypeId" />.
    /// </summary>
    internal static void EnsureQueryable(ICkCacheService ckCacheService, string tenantId,
        RtCkId<CkTypeId> ckTypeId, RtEntityQueryOptions queryOptions)
    {
        if (!TryGetSecretAwareType(ckCacheService, tenantId, ckTypeId, out var ckTypeGraph))
        {
            return;
        }

        var entityName = ckTypeId.ToString();
        EnsureFilterable(ckCacheService, tenantId, ckTypeGraph, entityName, queryOptions);

        foreach (var sortOrder in queryOptions.SortOrders ?? [])
        {
            EnsureNotSecret(ckCacheService, tenantId, ckTypeGraph, entityName, sortOrder.AttributePath,
                SortOperation);
        }

        foreach (var path in queryOptions.AttributeSearchFilter?.AttributePaths ?? [])
        {
            EnsureNotSecret(ckCacheService, tenantId, ckTypeGraph, entityName, path, AttributeSearchOperation);
        }

        if (queryOptions.ResultAggregation != null)
        {
            EnsureAggregationNotSecret(ckCacheService, tenantId, ckTypeGraph, entityName,
                queryOptions.ResultAggregation);
        }

        if (queryOptions.FieldAggregation != null)
        {
            foreach (var path in queryOptions.FieldAggregation.GroupByAttributePathList)
            {
                EnsureNotSecret(ckCacheService, tenantId, ckTypeGraph, entityName, path, GroupByOperation);
            }

            EnsureAggregationNotSecret(ckCacheService, tenantId, ckTypeGraph, entityName,
                queryOptions.FieldAggregation);
        }
    }

    internal const string QueryColumnOperation = "query column";

    /// <summary>
    ///     Called with the column paths a query could not resolve: the CK query columns never contain Secret
    ///     attributes (the collector excludes them), so a secret path would otherwise be reported as an
    ///     unknown column. Throws <see cref="SecretAttributeNotQueryableException" /> for the first secret path.
    /// </summary>
    internal static void EnsureNoSecretColumns(ICkCacheService ckCacheService, string tenantId,
        RtCkId<CkTypeId> ckTypeId, IEnumerable<string> columnPaths)
    {
        if (!TryGetSecretAwareType(ckCacheService, tenantId, ckTypeId, out var ckTypeGraph))
        {
            return;
        }

        foreach (var path in columnPaths)
        {
            EnsureNotSecret(ckCacheService, tenantId, ckTypeGraph, ckTypeId.ToString(),
                QueryColumnPathResolver.NormalizePath(path), QueryColumnOperation);
        }
    }

    /// <summary>
    ///     Validates field filters (e.g. the before/after filters of a subscription).
    /// </summary>
    internal static void EnsureFilterable(ICkCacheService ckCacheService, string tenantId,
        RtCkId<CkTypeId> ckTypeId, IEnumerable<FieldFilter>? fieldFilters)
    {
        if (fieldFilters == null || !TryGetSecretAwareType(ckCacheService, tenantId, ckTypeId, out var ckTypeGraph))
        {
            return;
        }

        foreach (var fieldFilter in fieldFilters)
        {
            EnsureFilterOperator(ckCacheService, tenantId, ckTypeGraph, ckTypeId.ToString(), fieldFilter);
        }
    }

    /// <summary>
    ///     Resolves an attribute path (camelCase or PascalCase, dotted through records) to its CK attribute,
    ///     or <c>null</c> when it is a navigation path or does not resolve.
    /// </summary>
    internal static CkTypeAttributeGraph? TryResolveAttribute(ICkCacheService ckCacheService, string tenantId,
        CkTypeWithAttributesGraph root, string? attributePath)
    {
        if (string.IsNullOrWhiteSpace(attributePath) || attributePath.Contains("->") || attributePath.Contains("::"))
        {
            return null;
        }

        var segments = IndexerRegex().Replace(attributePath, string.Empty)
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        CkTypeWithAttributesGraph current = root;
        for (var i = 0; i < segments.Length; i++)
        {
            if (!current.AllAttributesByName.TryGetValue(segments[i].ToPascalCase(), out var attribute))
            {
                return null;
            }

            if (i == segments.Length - 1)
            {
                return attribute;
            }

            if (attribute.ValueType is not (AttributeValueTypesDto.Record or AttributeValueTypesDto.RecordArray) ||
                attribute.ValueCkRecordId == null ||
                !ckCacheService.TryGetCkRecord(tenantId, attribute.ValueCkRecordId, out var recordGraph))
            {
                return null;
            }

            current = recordGraph;
        }

        return null;
    }

    /// <summary>
    ///     True when the path ends on a Secret attribute.
    /// </summary>
    internal static bool IsSecretPath(ICkCacheService ckCacheService, string tenantId,
        CkTypeWithAttributesGraph root, string? attributePath)
    {
        return TryResolveAttribute(ckCacheService, tenantId, root, attributePath)?.ValueType ==
               AttributeValueTypesDto.Secret;
    }

    private static void EnsureFilterable(ICkCacheService ckCacheService, string tenantId,
        CkTypeWithAttributesGraph ckTypeGraph, string entityName, FieldFilterCriteria criteria)
    {
        foreach (var fieldFilter in criteria.FieldFilters ?? [])
        {
            EnsureFilterOperator(ckCacheService, tenantId, ckTypeGraph, entityName, fieldFilter);
        }

        foreach (var nested in criteria.NestedFilters ?? [])
        {
            EnsureFilterable(ckCacheService, tenantId, ckTypeGraph, entityName, nested);
        }
    }

    private static void EnsureFilterOperator(ICkCacheService ckCacheService, string tenantId,
        CkTypeWithAttributesGraph ckTypeGraph, string entityName, FieldFilter fieldFilter)
    {
        if (fieldFilter.Operator is FieldFilterOperator.IsNull or FieldFilterOperator.IsNotNull)
        {
            return;
        }

        if (IsSecretPath(ckCacheService, tenantId, ckTypeGraph, fieldFilter.AttributePath))
        {
            throw new SecretAttributeNotQueryableException(fieldFilter.AttributePath,
                $"filter operator '{fieldFilter.Operator}'", entityName);
        }
    }

    private static void EnsureAggregationNotSecret(ICkCacheService ckCacheService, string tenantId,
        CkTypeWithAttributesGraph ckTypeGraph, string entityName, AggregationInput aggregationInput)
    {
        var paths = aggregationInput.CountAttributePathList
            .Concat(aggregationInput.MinValueAttributePathList)
            .Concat(aggregationInput.MaxValueAttributePathList)
            .Concat(aggregationInput.AvgAttributePathList)
            .Concat(aggregationInput.SumAttributePathList);
        foreach (var path in paths)
        {
            EnsureNotSecret(ckCacheService, tenantId, ckTypeGraph, entityName, path, AggregationOperation);
        }
    }

    private static void EnsureNotSecret(ICkCacheService ckCacheService, string tenantId,
        CkTypeWithAttributesGraph ckTypeGraph, string entityName, string attributePath, string operation)
    {
        if (IsSecretPath(ckCacheService, tenantId, ckTypeGraph, attributePath))
        {
            throw new SecretAttributeNotQueryableException(attributePath, operation, entityName);
        }
    }

    private static bool TryGetSecretAwareType(ICkCacheService ckCacheService, string tenantId,
        RtCkId<CkTypeId> ckTypeId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CkTypeGraph? ckTypeGraph)
    {
        // An unknown type is reported by the repository; the guard never turns it into a different error.
        return ckCacheService.TryGetRtCkType(tenantId, ckTypeId, out ckTypeGraph);
    }
}
