using GraphQL;
using GraphQL.Builders;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
/// GraphQL type that wraps <see cref="StreamDataTransientQueryDto"/>.
/// Exposes two sub-connections:
/// <list type="bullet">
///   <item><c>Rows</c> — executes the transient query via the stream-data engine.</item>
///   <item><c>Aggregations</c> — computes statistical aggregates over the same data set.</item>
/// </list>
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class StreamDataTransientQueryDtoType : ObjectGraphType<StreamDataTransientQueryDto>
{
    private readonly ILogger<StreamDataTransientQueryDtoType> _logger;

    /// <summary>Constructor.</summary>
    public StreamDataTransientQueryDtoType(ILogger<StreamDataTransientQueryDtoType> logger)
    {
        _logger = logger;
        Name = "StreamDataTransientQuery";
        Description = "Descriptor for a transient (ad-hoc) stream-data query. " +
                      "Use the Rows sub-connection to execute the query and the " +
                      "Aggregations sub-connection to compute statistics over the same data.";

        Field(d => d.QueryCkTypeId, typeof(NonNullGraphType<RtCkIdGraph<CkTypeId>>));
        Field(d => d.Columns, typeof(NonNullGraphType<ListGraphType<NonNullGraphType<RtQueryColumnType>>>));

        Connection<NonNullGraphType<StreamDataQueryRowDtoType>>("Rows")
            .Description("Executes the transient stream-data query and returns the result rows.")
            .Argument<StreamDataArgumentsGraphType>(Statics.StreamDataArgument,
                "Override time filter and limit at execution time.")
            .Argument<ListGraphType<SortDtoType>>(Statics.SortOrderArg,
                "Sort order for items (overrides the inline sort if supplied).")
            .ResolveAsync(ResolveRowsAsync);

        Connection<NonNullGraphType<QueryAggregationResultType>>("Aggregations")
            .Description("Computes statistical aggregations over the same data set as the transient query.")
            .Argument<NonNullGraphType<ResultAggregationInputDtoType>>(Statics.AggregationsArg,
                "Requested aggregation statistics.")
            .ResolveAsync(ResolveAggregationsAsync);
    }

    // ─── Rows resolver ───────────────────────────────────────────────────────

    private async Task<object?> ResolveRowsAsync(
        IResolveConnectionContext<StreamDataTransientQueryDto?> ctx)
    {
        try
        {
            _logger.LogDebug("StreamDataTransientQueryDtoType: executing Rows sub-connection");

            if (ctx.Source is not { } dto)
                throw AssetRepositoryException.SourceNotSet();

            if (dto.UserContext is not StreamDataTransientUserContext uc)
                throw AssetRepositoryException.UserContextNotSet();

            var gql = (GraphQlUserContext)ctx.UserContext;
            var tenantId = gql.TenantId;
            var streamDataRepo = gql.TenantContext.GetStreamDataRepository()
                ?? throw AssetRepositoryException.StreamDataNotAvailable();

            var ckTypeId = uc.CkTypeId;

            // Enum-id lookup for the target CK type, used to resolve raw integer enum keys to
            // their value names on the result cells (parity with the runtime query path).
            var enumIdResolver = StreamDataFieldResolverExtensions.BuildEnumColumnResolver(
                ctx.GetCkCacheService(), tenantId, ckTypeId);

            var archiveSnapshot = await gql.TenantContext.GetArchiveRuntimeStore().GetAsync(uc.ArchiveRtId)
                ?? throw new ArchiveNotFoundException(uc.ArchiveRtId);
            // Simple queries pick physical column paths directly; aggregation variants pick
            // logical CK paths (chain-walked for rollups) and the engine's chain-aware resolver
            // translates them to SQL. The resolver here drives both column-name mapping for the
            // result rows and field-validation, so it has to know the logical paths exist for
            // aggregation variants to validate successfully against rollup archives.
            var fieldResolver = uc.Variant == StreamQueryVariant.Simple
                ? BuildFieldResolver(archiveSnapshot)
                : await StreamDataFieldResolverExtensions.BuildAggregationFieldResolverAsync(
                    archiveSnapshot, gql, ctx.CancellationToken);

            IReadOnlyList<ColumnNameMapping> resolvedColumnNames;
            StreamQueryExecutionInput input;

            switch (uc.Variant)
            {
                case StreamQueryVariant.Simple:
                {
                    var columnPaths = uc.ColumnPaths ?? [];

                    // Allow runtime overrides for sort and time filter
                    ctx.TryGetArgument(Statics.SortOrderArg, out IEnumerable<SortDto>? runtimeSortDtos);
                    var runtimeSortList = runtimeSortDtos?.ToList();

                    var execOverride = ctx.GetArgument<StreamDataArguments?>(Statics.StreamDataArgument);

                    var sortOrders = runtimeSortList is { Count: > 0 }
                        ? StreamDataGraphQlMapper.MapSortOrders(runtimeSortList)
                        : uc.SortOrders;

                    resolvedColumnNames = fieldResolver.ResolveToMappings(columnPaths, enumIdResolver);

                    input = new StreamQueryExecutionInput
                    {
                        Variant = StreamQueryVariant.Simple,
                        ArchiveRtId = uc.ArchiveRtId,
                        CkTypeId = ckTypeId,
                        ColumnPaths = columnPaths,
                        RtIds = uc.RtIds,
                        From = execOverride?.From ?? uc.From,
                        To = execOverride?.To ?? uc.To,
                        Limit = execOverride?.Limit ?? uc.Limit,
                        SortOrders = sortOrders,
                        FieldFilters = uc.FieldFilters,
                        Offset = ctx.GetOffset(),
                        PageSize = ctx.First
                    };
                    break;
                }

                case StreamQueryVariant.Aggregation:
                {
                    var aggColumns = uc.AggregationColumns ?? [];

                    resolvedColumnNames = fieldResolver.ResolveAggregationMappings(aggColumns, enumIdResolver);

                    input = new StreamQueryExecutionInput
                    {
                        Variant = StreamQueryVariant.Aggregation,
                        ArchiveRtId = uc.ArchiveRtId,
                        CkTypeId = ckTypeId,
                        AggregationColumns = aggColumns,
                        RtIds = uc.RtIds,
                        From = uc.From,
                        To = uc.To,
                        FieldFilters = uc.FieldFilters
                    };
                    break;
                }

                case StreamQueryVariant.GroupingAggregation:
                {
                    var groupByPaths = uc.GroupByColumnPaths ?? [];
                    var aggColumns = uc.AggregationColumns ?? [];

                    resolvedColumnNames = fieldResolver
                        .ResolveToMappings(groupByPaths, enumIdResolver)
                        .Concat(fieldResolver.ResolveAggregationMappings(aggColumns, enumIdResolver))
                        .ToList();

                    input = new StreamQueryExecutionInput
                    {
                        Variant = StreamQueryVariant.GroupingAggregation,
                        ArchiveRtId = uc.ArchiveRtId,
                        CkTypeId = ckTypeId,
                        GroupByColumnPaths = groupByPaths,
                        AggregationColumns = aggColumns,
                        RtIds = uc.RtIds,
                        From = uc.From,
                        To = uc.To,
                        FieldFilters = uc.FieldFilters
                    };
                    break;
                }

                case StreamQueryVariant.Downsampling:
                {
                    var aggColumns = uc.AggregationColumns ?? [];

                    // The downsampling engine path always surfaces the bin time under the canonical
                    // `timestamp` key; build that mapping directly rather than via ResolveToMappings,
                    // which throws on windowed-storage archives (their resolver has no `timestamp`
                    // default — the time axis is window_end). Aggregation columns follow with
                    // function-suffixed keys (matches engine MapAggregationRow).
                    resolvedColumnNames = new List<ColumnNameMapping>
                        {
                            new(Constants.Timestamp, Constants.Timestamp)
                        }
                        .Concat(fieldResolver.ResolveAggregationMappings(aggColumns, enumIdResolver))
                        .ToList();

                    input = new StreamQueryExecutionInput
                    {
                        Variant = StreamQueryVariant.Downsampling,
                        ArchiveRtId = uc.ArchiveRtId,
                        CkTypeId = ckTypeId,
                        AggregationColumns = aggColumns,
                        GroupByColumnPaths = uc.GroupByColumnPaths,
                        RtIds = uc.RtIds,
                        From = uc.From,
                        To = uc.To,
                        Limit = uc.Limit,
                        FieldFilters = uc.FieldFilters
                    };
                    break;
                }

                default:
                    throw new InvalidOperationException(
                        $"Unsupported StreamQueryVariant: {uc.Variant}");
            }

            var result = await StreamDataVariantExecutor.ExecuteAsync(streamDataRepo, input);

            _logger.LogDebug(
                "StreamDataTransientQueryDtoType Rows: got {Count} rows, totalCount={Total}",
                result.Rows.Count, result.TotalCount);

            var rows = result.Rows
                .Select(r => StreamDataQueryRowDto.FromStreamDataRow(r, resolvedColumnNames))
                .ToList();

            var effectiveOffset = input.Offset.GetValueOrDefault(0);
            return ConnectionUtils.ToOctoConnection(rows, ctx,
                rows.Count != 0 ? effectiveOffset : 0, (int)result.TotalCount);
        }
        catch (Exception e)
        {
            return ctx.HandleException(e);
        }
    }

    // ─── Aggregations resolver ────────────────────────────────────────────────

    private async Task<object?> ResolveAggregationsAsync(
        IResolveConnectionContext<StreamDataTransientQueryDto?> ctx)
    {
        try
        {
            _logger.LogDebug(
                "StreamDataTransientQueryDtoType: executing Aggregations sub-connection");

            if (ctx.Source is not { } dto)
                throw AssetRepositoryException.SourceNotSet();

            if (dto.UserContext is not StreamDataTransientUserContext uc)
                throw AssetRepositoryException.UserContextNotSet();

            var gql = (GraphQlUserContext)ctx.UserContext;
            var streamDataRepo = gql.TenantContext.GetStreamDataRepository()
                ?? throw AssetRepositoryException.StreamDataNotAvailable();

            var ckTypeId = uc.CkTypeId;
            var aggInput = ctx.GetArgument<ResultAggregationInputDto>(Statics.AggregationsArg);

            // Build aggregation columns from the requested stats input.
            // Each (stat × path) pair becomes one AggregationColumn.
            var aggColumns = new List<AggregationColumn>();
            if (aggInput.CountAttributePaths != null)
                aggColumns.AddRange(aggInput.CountAttributePaths.Select(p =>
                    new AggregationColumn(p, AggregationFunction.Count)));
            if (aggInput.MinValueAttributePaths != null)
                aggColumns.AddRange(aggInput.MinValueAttributePaths.Select(p =>
                    new AggregationColumn(p, AggregationFunction.Minimum)));
            if (aggInput.MaxValueAttributePaths != null)
                aggColumns.AddRange(aggInput.MaxValueAttributePaths.Select(p =>
                    new AggregationColumn(p, AggregationFunction.Maximum)));
            if (aggInput.AvgAttributePaths != null)
                aggColumns.AddRange(aggInput.AvgAttributePaths.Select(p =>
                    new AggregationColumn(p, AggregationFunction.Average)));
            if (aggInput.SumAttributePaths != null)
                aggColumns.AddRange(aggInput.SumAttributePaths.Select(p =>
                    new AggregationColumn(p, AggregationFunction.Sum)));

            var input = new StreamQueryExecutionInput
            {
                Variant = StreamQueryVariant.Aggregation,
                ArchiveRtId = uc.ArchiveRtId,
                CkTypeId = ckTypeId,
                AggregationColumns = aggColumns,
                RtIds = uc.RtIds,
                From = uc.From,
                To = uc.To,
                FieldFilters = uc.FieldFilters
            };

            var result = await StreamDataVariantExecutor.ExecuteAsync(streamDataRepo, input);

            _logger.LogDebug(
                "StreamDataTransientQueryDtoType Aggregations: got {Count} agg rows",
                result.Rows.Count);

            // Project the single aggregation row into QueryAggregationResult.
            var countStats = StreamDataStatisticsHelper.BuildStats(aggInput.CountAttributePaths, result, "Count");
            var minStats   = StreamDataStatisticsHelper.BuildStats(aggInput.MinValueAttributePaths, result, "Min");
            var maxStats   = StreamDataStatisticsHelper.BuildStats(aggInput.MaxValueAttributePaths, result, "Max");
            var avgStats   = StreamDataStatisticsHelper.BuildStats(aggInput.AvgAttributePaths, result, "Avg");
            var sumStats   = StreamDataStatisticsHelper.BuildStats(aggInput.SumAttributePaths, result, "Sum");

            var totalCount = result.TotalCount;

            var aggregationResult = new QueryAggregationResult(
                totalCount,
                countStats,
                minStats,
                maxStats,
                avgStats,
                sumStats,
                null);

            return ConnectionUtils.ToOctoConnection(
                new[] { aggregationResult }, ctx, 0, 1);
        }
        catch (Exception e)
        {
            return ctx.HandleException(e);
        }
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static StreamDataFieldResolver BuildFieldResolver(ArchiveSnapshot archiveSnapshot)
    {
        // Per-archive table contents are bounded by the archive's column spec. The factory
        // handles computed columns (empty Path, versioned physical name).
        return StreamDataFieldResolver.CreateForArchive(archiveSnapshot);
    }
}
