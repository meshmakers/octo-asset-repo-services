using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

internal static class StreamDataFieldResolverExtensions
{
    /// <summary>
    /// Resolves each flat attribute name to a canonical+wire pair for cells-based
    /// stream-data resolvers. Null-forgives the resolver result because callers
    /// are expected to have validated column paths beforehand via
    /// StreamDataFieldValidation.
    /// </summary>
    public static IReadOnlyList<ColumnNameMapping> ResolveToMappings(
        this StreamDataFieldResolver resolver,
        IEnumerable<string> columns)
    {
        return columns.Select(c =>
        {
            var r = resolver.Resolve(c)!;
            return new ColumnNameMapping(r.CrateDbName, r.GraphQlAlias);
        }).ToList();
    }

    /// <summary>
    /// Builds a field resolver for aggregation-style queries (Aggregation / GroupingAggregation /
    /// Downsampling) on a rollup archive. Extends the resolver's data-stream attribute set with
    /// the *logical* CK-attribute paths recovered by walking the rollup's source-archive chain —
    /// without this, the field-validation step rejects the operator's logical paths (e.g.
    /// <c>amount.value</c>) because the archive table only physically has the materialised
    /// storage columns (<c>amountvalue_sum</c>, …). The chain-aware aggregation resolver
    /// translates the logical paths to SQL on the engine side. For raw / time-range archives
    /// (or rollups whose chain can't be loaded for any reason) this falls back to the
    /// physical-only resolver.
    ///
    /// Not used for Simple queries — those still pick physical column names directly, and adding
    /// logical paths there would defer the failure to SQL execution.
    /// </summary>
    public static async Task<StreamDataFieldResolver> BuildAggregationFieldResolverAsync(
        ArchiveSnapshot archiveSnapshot,
        GraphQlUserContext gql,
        CancellationToken cancellationToken)
    {
        var paths = archiveSnapshot.Columns.Select(c => c.Path).ToList();

        if (archiveSnapshot.RollupAggregations is not null)
        {
            var rollupStore = gql.TenantContext.GetRollupArchiveRuntimeStore();
            var archiveStore = gql.TenantContext.GetArchiveRuntimeStore();
            if (rollupStore is not null)
            {
                var rollup = await rollupStore.GetAsync(archiveSnapshot.RtId).ConfigureAwait(false);
                if (rollup is not null)
                {
                    var logical = await RollupLogicalPathResolver.ResolveAsync(
                        rollup,
                        id => archiveStore.GetAsync(id),
                        id => rollupStore.GetAsync(id),
                        cancellationToken).ConfigureAwait(false);
                    paths.AddRange(logical);
                }
            }
        }

        return new StreamDataFieldResolver(
            paths,
            usesWindowedStorage: archiveSnapshot.UsesWindowedStorage);
    }
}
