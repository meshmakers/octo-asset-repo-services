using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

internal static class StreamDataFieldResolverExtensions
{
    /// <summary>
    /// Builds a path → CK-enum-id lookup for the given target CK type, used to enrich
    /// <see cref="ColumnNameMapping"/> so cells-based stream-data resolvers can resolve raw
    /// integer enum keys to their value names (parity with the runtime query path). The lookup
    /// is keyed by the same attribute-path string the persisted/transient query columns use, so
    /// a column path resolves to its enum id iff the CK attribute at that path is enum-typed.
    /// Degrades to a null-result resolver (raw integers, no resolution) if the CK type columns
    /// cannot be enumerated — enum-name resolution is a display enhancement and must never break
    /// an otherwise-valid query.
    /// </summary>
    public static Func<string, CkId<CkEnumId>?> BuildEnumColumnResolver(
        ICkCacheService ckCacheService, string tenantId, RtCkId<CkTypeId> ckTypeId)
    {
        // Case-insensitive: stream-data column/query paths are PascalCase (e.g. "OperatingStatus",
        // mirroring the archive column spec), whereas the CK query-column collector emits the
        // attribute path camelCase ("operatingStatus"). Same attribute, first-letter casing differs
        // — match the whole dotted path ignoring case. Indexer-set (not ToDictionary) so a rare
        // case-only path collision overwrites instead of throwing.
        var enumColumns = new Dictionary<string, CkId<CkEnumId>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // Stream-data archives only carry physical columns — skip navigation expansion,
            // which explodes combinatorially on densely connected CK models (see the transient
            // stream-data resolvers). Enum-typed columns are always direct attributes.
            foreach (var c in ckCacheService.GetCkTypeQueryColumnPathsByRtCkId(tenantId, ckTypeId,
                         new CkTypeQueryColumnOptions { IgnoreNavigationProperties = true }))
            {
                if (c.ValueType == AttributeValueTypesDto.Enum && c.CkEnumId != null)
                {
                    enumColumns[c.Path] = c.CkEnumId;
                }
            }
        }
        catch
        {
            enumColumns.Clear();
        }

        return path => enumColumns.GetValueOrDefault(path);
    }

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
        IEnumerable<string> columns,
        Func<string, CkId<CkEnumId>?>? enumIdResolver = null)
    {
        return columns.Select(c =>
        {
            var r = resolver.Resolve(c)!;
            return new ColumnNameMapping(r.CrateDbName, c, enumIdResolver?.Invoke(c));
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
        IEnumerable<AggregationColumn> aggregationColumns,
        Func<string, CkId<CkEnumId>?>? enumIdResolver = null)
    {
        return aggregationColumns.Select(col =>
        {
            var r = resolver.Resolve(col.AttributePath)!;
            var suffix = AggregationFunctionWireSuffix(col.Function);
            var key = $"{r.CrateDbName}_{suffix}";
            // Only value-preserving reducers keep the original enum key: MIN/MAX of an enum
            // column return one of the source integer keys, so resolving to its name is correct.
            // COUNT/SUM/AVG produce derived numbers that are not enum keys — leave them raw.
            var enumId = col.Function is AggregationFunction.Minimum or AggregationFunction.Maximum
                ? enumIdResolver?.Invoke(col.AttributePath)
                : null;
            return new ColumnNameMapping(key, key, enumId);
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
        // Short token, matching the engine's chain-resolver alias ("{path}_twavg") and the
        // rollup column naming (AB#4336 decision D5) — the enum name would drift to
        // "timeweightedaverage" and the cell lookup would miss the engine's output key.
        AggregationFunction.TimeWeightedAverage => "twavg",
        // "stateduration" also equals the enum-name fallback, but be explicit so the alias
        // contract stays visible next to the engine's "{col}_stateduration" output (AB#4341).
        AggregationFunction.StateDuration => "stateduration",
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
