using System.Collections;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;

/// <summary>
///     AB#8049: refuses to store a query (<c>System/PersistentQuery</c> and every type derived from it) whose field
///     filters, sort orders, columns, group-by columns or attribute-search paths reach a CK v2 <c>Hidden</c> attribute.
///     Running such a query through GraphQL is already refused (<see cref="AccessQueryGuard" />), but the repository
///     applies a stored filter as it is, so the export job and the pipeline nodes would turn it into an oracle for the
///     hidden value (the number of rows answers the guess). Those runners check the stored query themselves (a query can
///     also arrive through an import or a restore); refusing it here gives the author a clear error at the moment of
///     saving.
/// </summary>
/// <remarks>
///     Applies to inserts, updates and replaces of the generic and the typed mutations (<see cref="RtMutationBase" />).
///     On an update the effective query is checked: the attributes of the update over the stored entity, so changing
///     only <c>QueryCkTypeId</c> under an existing filter is judged too. The error is <c>ATTRIBUTE_NOT_QUERYABLE</c>,
///     names the path (selector values removed) and the query type, never a comparison value.
/// </remarks>
internal static class StoredQueryWriteGuard
{
    internal const string FilterOperation = "a stored query filter";
    internal const string SortOperation = "a stored query sort order";
    internal const string ColumnOperation = "a stored query column";
    internal const string GroupByOperation = "a stored query group-by column";
    internal const string AttributeSearchOperation = "a stored query attribute search";

    private const string QueryCkTypeIdAttribute = "QueryCkTypeId";
    private const string FieldFilterAttribute = "FieldFilter";
    private const string SortingAttribute = "Sorting";
    private const string ColumnsAttribute = "Columns";
    private const string GroupingColumnsAttribute = "GroupingColumns";
    private const string AttributeSearchFilterAttribute = "AttributeSearchFilter";
    private const string AttributePathMember = "AttributePath";
    private const string AttributePathsMember = "AttributePaths";

    private static readonly string[] PathAttributes =
    [
        QueryCkTypeIdAttribute, FieldFilterAttribute, SortingAttribute, ColumnsAttribute, GroupingColumnsAttribute,
        AttributeSearchFilterAttribute
    ];

    /// <summary>Throws <see cref="HiddenAttributeAccessException" /> for the first stored query path that reaches Hidden.</summary>
    internal static async Task EnsureStoredQueriesAllowedAsync(ICkCacheService ckCacheService,
        ITenantRepository repository, IOctoSession session, IEnumerable<EntityUpdateInfo<RtEntity>> entityUpdateInfos)
    {
        foreach (var updateInfo in entityUpdateInfos)
        {
            if (updateInfo.ModOption is not (EntityModOptions.Insert or EntityModOptions.Update
                    or EntityModOptions.Replace) || updateInfo.RtEntity == null ||
                !IsPersistentQuery(ckCacheService, repository.TenantId, updateInfo.CkTypeId))
            {
                continue;
            }

            var written = updateInfo.RtEntity;
            if (updateInfo.ModOption != EntityModOptions.Insert && !PathAttributes.Any(written.Attributes.ContainsKey))
            {
                continue; // e.g. a rename: no path changes
            }

            RtEntity? stored = null;
            if (updateInfo.ModOption == EntityModOptions.Update && updateInfo.RtId is { } rtId)
            {
                stored = await repository.GetRtEntityByRtIdAsync(session, new RtEntityId(updateInfo.CkTypeId, rtId));
            }

            object? Effective(string attribute) =>
                written.Attributes.TryGetValue(attribute, out var value) ? value :
                stored != null && stored.Attributes.TryGetValue(attribute, out var storedValue) ? storedValue : null;

            var paths = CollectPaths(Effective).ToList();
            if (paths.Count == 0)
            {
                continue;
            }

            var queryCkTypeId = Effective(QueryCkTypeIdAttribute) as string;
            AccessQueryGuard.EnsureStoredQueryPathsAllowed(ckCacheService, repository.TenantId,
                string.IsNullOrWhiteSpace(queryCkTypeId) ? null : new RtCkId<CkTypeId>(queryCkTypeId), paths);
        }
    }

    /// <summary>Every path a stored query carries, with what it is used for.</summary>
    internal static IEnumerable<(string Path, string Operation)> CollectPaths(Func<string, object?> attribute)
    {
        foreach (var path in RecordMembers(attribute(FieldFilterAttribute), AttributePathMember))
        {
            yield return (path, FilterOperation);
        }

        foreach (var path in RecordMembers(attribute(SortingAttribute), AttributePathMember))
        {
            yield return (path, SortOperation);
        }

        // Columns: a string array (simple queries) or aggregation column records (AttributePath).
        foreach (var path in Strings(attribute(ColumnsAttribute))
                     .Concat(RecordMembers(attribute(ColumnsAttribute), AttributePathMember)))
        {
            yield return (path, ColumnOperation);
        }

        foreach (var path in Strings(attribute(GroupingColumnsAttribute)))
        {
            yield return (path, GroupByOperation);
        }

        if (attribute(AttributeSearchFilterAttribute) is RtRecord search)
        {
            foreach (var path in Strings(Member(search, AttributePathsMember)))
            {
                yield return (path, AttributeSearchOperation);
            }
        }
    }

    private static bool IsPersistentQuery(ICkCacheService ckCacheService, string tenantId, RtCkId<CkTypeId> ckTypeId)
    {
        if (!ckCacheService.TryGetRtCkType(tenantId, ckTypeId, out var graph) || graph == null)
        {
            return false;
        }

        return graph.GetBaseTypes(true).Any(t => t.ToRtCkId() == SystemCkIds.RtCkPersistentQueryTypeId);
    }

    private static IEnumerable<string> Strings(object? value) => value switch
    {
        null => [],
        string single => [single],
        IEnumerable items => items.OfType<string>().Where(s => !string.IsNullOrWhiteSpace(s)),
        _ => []
    };

    private static IEnumerable<string> RecordMembers(object? value, string member) => value switch
    {
        RtRecord record => Strings(Member(record, member)),
        IEnumerable items and not string => items.OfType<RtRecord>().SelectMany(r => Strings(Member(r, member))),
        _ => []
    };

    private static object? Member(RtRecord record, string member) =>
        record.Attributes.TryGetValue(member, out var value)
            ? value
            : record.Attributes.FirstOrDefault(a => string.Equals(a.Key, member, StringComparison.OrdinalIgnoreCase))
                .Value;
}
