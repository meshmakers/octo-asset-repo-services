using System.Text.RegularExpressions;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;

/// <summary>
/// Resolves stored/requested query column paths against the collector's column set.
/// Handles the AB#4323 extensions: value columns across inbound/N-multiplicity associations
/// (opt-in in the collector because of their fan-out) and entity selectors
/// (<c>nav.type[key=value]-&gt;attr</c>), which never appear in collector output and must be
/// stripped for matching but preserved for cell evaluation.
/// </summary>
internal static partial class QueryColumnPathResolver
{
    [GeneratedRegex(@"\[[^\[\]=]+=[^\[\]]*\]", RegexOptions.Compiled)]
    private static partial Regex EntitySelectorRegex();

    /// <summary>
    /// Strips entity selectors (<c>[key=value]</c>) from a column path for matching against
    /// collector-generated paths. Array indexes (<c>[0]</c>, <c>[*]</c>) are kept.
    /// </summary>
    internal static string NormalizePath(string path)
    {
        return EntitySelectorRegex().Replace(path, string.Empty);
    }

    /// <summary>
    /// Returns the collector columns for a query type, bounded to the navigation depth the
    /// requested paths actually need. Column enumeration must NEVER run with unbounded depth
    /// here: on densely connected models (self-association on a root type + derived-type
    /// fan-out, AB#4320) unbounded expansion trips the engine's MaxColumns fail-fast cap and
    /// fails EVERY query on such a type — a plain rtId/name query needs depth 0.
    /// When one of the requested paths is not covered by the depth-bounded default set but
    /// crosses a navigation, the collection is repeated with
    /// <see cref="CkTypeQueryColumnOptions.IncludeManyNavigations"/> enabled, so the inbound/N
    /// fan-out only costs where a query genuinely uses such columns.
    /// </summary>
    internal static IReadOnlyCollection<CkTypeQueryColumn> GetColumnsForPaths(
        ICkCacheService ckCacheService, string tenantId, RtCkId<CkTypeId> queryCkTypeId,
        IReadOnlyCollection<string> requestedPaths)
    {
        var normalizedPaths = requestedPaths.Select(NormalizePath).ToList();
        var requiredDepth = normalizedPaths.Count == 0
            ? 0
            : normalizedPaths.Max(RequiredNavigationDepth);

        var columns = ckCacheService.GetCkTypeQueryColumnPathsByRtCkId(tenantId, queryCkTypeId,
            new CkTypeQueryColumnOptions { MaxDepth = requiredDepth });

        var hasUncoveredNavigationPath = normalizedPaths.Any(p =>
            p.Contains("->") && columns.All(c => c.Path != p));
        if (!hasUncoveredNavigationPath)
        {
            return columns;
        }

        return ckCacheService.GetCkTypeQueryColumnPathsByRtCkId(tenantId, queryCkTypeId,
            new CkTypeQueryColumnOptions
            {
                IncludeManyNavigations = true,
                MaxDepth = Math.Max(1, requiredDepth)
            });
    }

    /// <summary>
    /// The navigation depth the collector must expand to for this (normalized) path to be
    /// produced: one level per <c>-&gt;</c> segment; association meta columns
    /// (<c>::totalCount</c>/<c>::exists</c>) are emitted at the first navigation level;
    /// plain attribute paths need none.
    /// </summary>
    private static int RequiredNavigationDepth(string normalizedPath)
    {
        var navigationDepth = normalizedPath.Split(["->"], StringSplitOptions.None).Length - 1;
        return normalizedPath.Contains("::") ? Math.Max(1, navigationDepth) : navigationDepth;
    }

    /// <summary>
    /// A value-navigation pair crossing an N-multiplicity association (AB#4323): it resolves
    /// per row to the first matching target — rows without a match must not be filtered out,
    /// so the query switches to <c>NavigationFilterMode.Include</c>. Count/exists pairs carry
    /// an AssociationCountFilter and are handled separately.
    /// </summary>
    internal static bool IsFirstMatchValueNavigation(NavigationPair pair,
        ICkCacheService ckCacheService, string tenantId)
    {
        if (pair.AssociationCountFilter != null)
        {
            return false;
        }

        var associationRole = ckCacheService.GetRtCkAssociationRole(tenantId, pair.CkRoleId);
        return pair.Direction == GraphDirections.Inbound
            ? associationRole.InboundMultiplicity == MultiplicitiesDto.N
            : associationRole.OutboundMultiplicity == MultiplicitiesDto.N;
    }

    /// <summary>
    /// Finds the collector column matching a stored path. When the stored path carries an
    /// entity selector, the returned column is a clone whose <c>Path</c> and
    /// <c>AccessPathList</c> preserve the selector so the in-memory walker applies it during
    /// cell evaluation.
    /// </summary>
    internal static CkTypeQueryColumn? TryResolveColumn(
        IReadOnlyCollection<CkTypeQueryColumn> columns, string storedPath)
    {
        var normalizedPath = NormalizePath(storedPath);
        var column = columns.FirstOrDefault(c => c.Path == normalizedPath);
        if (column == null || normalizedPath == storedPath)
        {
            return column;
        }

        var accessPathList = RtPathEvaluator.TokenizePath(storedPath);
        return column.CkEnumId != null
            ? new CkTypeQueryColumn(storedPath, accessPathList, column.CkEnumId, column.Description)
            : new CkTypeQueryColumn(storedPath, accessPathList, column.ValueType, column.AssociationTuple,
                column.Description);
    }
}
