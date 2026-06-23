using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
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
    /// <para>
    /// The wire alias echoes the caller's requested column verbatim — same contract as the
    /// runtime <c>RtSimpleQueryCellDto</c> path. Clients (Refinery Studio query result grid,
    /// MCP tool consumers, Power BI connector) bind grid columns to the saved query's column
    /// strings; if the wire diverges from the input the grid lookup fails silently and cells
    /// render empty even though the row count is correct.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ColumnNameMapping> ResolveToMappings(
        this StreamDataFieldResolver resolver,
        IEnumerable<string> columns)
    {
        return columns.Select(c =>
        {
            var r = resolver.Resolve(c)!;
            return new ColumnNameMapping(r.CrateDbName, c);
        }).ToList();
    }

    /// <summary>
    /// Aggregation-aware variant of <see cref="ResolveToMappings"/>. Each aggregation column
    /// produces a mapping whose canonical / wire key includes a lowercase function suffix
    /// (e.g. <c>amountvalue_min</c>) so multiple aggregations on the same attribute path don't
    /// collide on the same row.Values key. The engine's MapAggregationRow stores results under
    /// this same key for parity. Mirrors `CrateDbStreamDataRepository.Execute*AggregationQueryAsync`.
    /// </summary>
    public static IReadOnlyList<ColumnNameMapping> ResolveAggregationMappings(
        this StreamDataFieldResolver resolver,
        IEnumerable<AggregationColumn> aggregationColumns)
    {
        return aggregationColumns.Select(col =>
        {
            var r = resolver.Resolve(col.AttributePath)!;
            var suffix = AggregationFunctionWireSuffix(col.Function);
            var key = $"{r.CrateDbName}_{suffix}";
            return new ColumnNameMapping(key, key);
        }).ToList();
    }

    /// <summary>
    /// Function-suffix the engine appends to <c>row.Values</c> keys (see
    /// `CrateDbStreamDataRepository.Execute*AggregationQueryAsync`). The engine talks in
    /// <c>AggregationFunctionDto</c> short names (Min/Max/Avg/Sum/Count); the wire DTO uses
    /// <c>AggregationFunction</c> long names (Minimum/Maximum/Average/Sum/Count) — translate
    /// so both sides land on the same key.
    /// </summary>
    private static string AggregationFunctionWireSuffix(AggregationFunction f) => f switch
    {
        AggregationFunction.Minimum => "min",
        AggregationFunction.Maximum => "max",
        AggregationFunction.Average => "avg",
        AggregationFunction.Sum => "sum",
        AggregationFunction.Count => "count",
        _ => f.ToString().ToLowerInvariant()
    };

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
