using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;

/// <summary>
///     CK v2 (AB#5668): keeps attributes with <c>access: Hidden</c> out of every runtime query path (columns, filters,
///     sort, search, aggregations, group-by) and out of query-row writes. The engine's query column collector does not
///     know about access, so the asset-repo filters its output.
///     <para>
///         Plain and record paths (<c>passwordHash</c>, <c>address.street</c>) are resolved exactly against the query
///         type. A path that crosses a navigation (<c>-&gt;</c>) is checked by the name of its final attribute: it is
///         treated as hidden when any type, record or association role of the tenant assigns an attribute of that name
///         as <c>Hidden</c> (conservative; Phase 0 only hides <c>PasswordHash</c> and <c>SecurityStamp</c>).
///     </para>
/// </summary>
internal static class AccessQueryGuard
{
    internal const string QueryColumnOperation = "query column";
    internal const string QueryRowWriteOperation = "query row update";

    internal static bool IsHidden(CkTypeAttributeGraph? attribute)
    {
        return attribute != null && !AttributeAccess.IsExposedInOutput(attribute.Access);
    }

    /// <summary>
    ///     True when the attribute path ends on a hidden attribute.
    /// </summary>
    internal static bool IsHiddenPath(ICkCacheService ckCacheService, string tenantId,
        CkTypeWithAttributesGraph root, string? attributePath)
    {
        return IsHiddenPath(ckCacheService, tenantId, root, attributePath, null);
    }

    private static bool IsHiddenPath(ICkCacheService ckCacheService, string tenantId,
        CkTypeWithAttributesGraph root, string? attributePath, IReadOnlySet<string>? hiddenNames)
    {
        if (string.IsNullOrWhiteSpace(attributePath))
        {
            return false;
        }

        var normalized = QueryColumnPathResolver.NormalizePath(attributePath);
        if (!normalized.Contains("->"))
        {
            return IsHidden(SecretQueryGuard.TryResolveAttribute(ckCacheService, tenantId, root, normalized));
        }

        var lastSegment = normalized[(normalized.LastIndexOf("->", StringComparison.Ordinal) + 2)..];
        if (lastSegment.Contains("::"))
        {
            return false; // association meta column (totalCount / exists)
        }

        var attributeName = lastSegment.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (attributeName == null)
        {
            return false;
        }

        attributeName = attributeName.Split('[')[0].ToPascalCase();
        return (hiddenNames ?? GetHiddenAttributeNames(ckCacheService, tenantId)).Contains(attributeName);
    }

    /// <summary>
    ///     Removes the columns whose path ends on a hidden attribute.
    /// </summary>
    internal static IReadOnlyCollection<CkTypeQueryColumn> WithoutHiddenColumns(ICkCacheService ckCacheService,
        string tenantId, RtCkId<CkTypeId> ckTypeId, IReadOnlyCollection<CkTypeQueryColumn> columns)
    {
        var hiddenNames = GetHiddenAttributeNames(ckCacheService, tenantId);
        if (hiddenNames.Count == 0 || !ckCacheService.TryGetRtCkType(tenantId, ckTypeId, out var ckTypeGraph))
        {
            return columns;
        }

        return columns.Where(c => !IsHiddenPath(ckCacheService, tenantId, ckTypeGraph, c.Path, hiddenNames))
            .ToList();
    }

    /// <summary>
    ///     Throws <see cref="HiddenAttributeAccessException" /> for the first path that ends on a hidden attribute.
    /// </summary>
    internal static void EnsureNoHiddenColumns(ICkCacheService ckCacheService, string tenantId,
        RtCkId<CkTypeId> ckTypeId, IEnumerable<string> paths, string operation, bool isWrite = false)
    {
        var hiddenNames = GetHiddenAttributeNames(ckCacheService, tenantId);
        if (hiddenNames.Count == 0 || !ckCacheService.TryGetRtCkType(tenantId, ckTypeId, out var ckTypeGraph))
        {
            return;
        }

        foreach (var path in paths)
        {
            if (IsHiddenPath(ckCacheService, tenantId, ckTypeGraph, path, hiddenNames))
            {
                throw isWrite
                    ? HiddenAttributeAccessException.NotWritable(path, ckTypeId.ToString(), CkAttributeAccessDto.Hidden)
                    : HiddenAttributeAccessException.NotQueryable(path, ckTypeId.ToString(), operation);
            }
        }
    }

    /// <summary>
    ///     Throws <see cref="HiddenAttributeAccessException" /> when the path ends on a hidden attribute (used by
    ///     <see cref="SecretQueryGuard" /> for filters, sort, search, aggregations and group-by).
    /// </summary>
    internal static void EnsureNotHidden(ICkCacheService ckCacheService, string tenantId,
        CkTypeWithAttributesGraph root, string entityName, string? attributePath, string operation)
    {
        if (attributePath == null)
        {
            return;
        }

        var hiddenNames = GetHiddenAttributeNames(ckCacheService, tenantId);
        if (hiddenNames.Count > 0 && IsHiddenPath(ckCacheService, tenantId, root, attributePath, hiddenNames))
        {
            throw HiddenAttributeAccessException.NotQueryable(attributePath, entityName, operation);
        }
    }

    private static IReadOnlySet<string> GetHiddenAttributeNames(ICkCacheService ckCacheService, string tenantId)
    {
        if (!ckCacheService.IsTenantLoaded(tenantId))
        {
            return new HashSet<string>();
        }

        var attributes = ckCacheService.GetCkTypes(tenantId).SelectMany(t => t.AllAttributes.Values)
            .Concat(ckCacheService.GetCkRecords(tenantId).SelectMany(r => r.AllAttributes.Values));
        return attributes.Where(IsHidden).Select(a => a.AttributeName).ToHashSet(StringComparer.Ordinal);
    }
}
