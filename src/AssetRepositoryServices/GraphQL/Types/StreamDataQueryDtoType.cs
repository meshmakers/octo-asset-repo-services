using GraphQL;
using GraphQL.Builders;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
/// GraphQL type that wraps <see cref="StreamDataQueryDto"/>.
/// Exposes three scalar fields (<c>queryRtId</c>, <c>associatedCkTypeId</c>, <c>columns</c>)
/// plus two sub-connections:
/// <list type="bullet">
///   <item><c>Rows</c> — dispatches to the correct stream-data engine call based on the
///     runtime type of the loaded <see cref="RtStreamDataQuery"/>.</item>
///   <item><c>Aggregations</c> — computes statistical aggregates via a second engine call
///     using the same filter set.</item>
/// </list>
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class StreamDataQueryDtoType : ObjectGraphType<StreamDataQueryDto>
{
    private readonly ILogger<StreamDataQueryDtoType> _logger;

    /// <summary>Constructor.</summary>
    public StreamDataQueryDtoType(ILogger<StreamDataQueryDtoType> logger)
    {
        _logger = logger;
        Name = "StreamDataQuery";
        Description = "Descriptor for a persisted stream-data query. " +
                      "Use the Rows sub-connection to execute the query and the " +
                      "Aggregations sub-connection to compute statistics over the same data.";

        Field(d => d.QueryRtId, typeof(NonNullGraphType<OctoObjectIdType>));
        Field(d => d.AssociatedCkTypeId, typeof(NonNullGraphType<RtCkIdGraph<CkTypeId>>));
        Field(d => d.Columns, typeof(NonNullGraphType<ListGraphType<NonNullGraphType<RtQueryColumnType>>>));

        Connection<NonNullGraphType<StreamDataQueryRowDtoType>>("Rows")
            .Description("Executes the persisted stream-data query and returns the result rows. " +
                         "Accepts optional runtime overrides for the time range, limit, and sort order.")
            .Argument<StreamDataArgumentsGraphType>(Statics.StreamDataArgument,
                "Override time filter and limit at execution time.")
            .Argument<ListGraphType<SortDtoType>>(Statics.SortOrderArg,
                "Sort order for items (overrides persisted sort if supplied).")
            .ResolveAsync(ResolveRowsAsync);

        Connection<NonNullGraphType<QueryAggregationResultType>>("Aggregations")
            .Description("Computes statistical aggregations over the same data set as the persisted query.")
            .Argument<NonNullGraphType<ResultAggregationInputDtoType>>(Statics.AggregationsArg,
                "Requested aggregation statistics.")
            .ResolveAsync(ResolveAggregationsAsync);
    }

    // ─── Rows resolver ───────────────────────────────────────────────────────

    private async Task<object?> ResolveRowsAsync(IResolveConnectionContext<StreamDataQueryDto?> ctx)
    {
        try
        {
            _logger.LogDebug("StreamDataQueryDtoType: executing Rows sub-connection");

            if (ctx.Source is not { } dto)
                throw AssetRepositoryException.SourceNotSet();

            if (dto.UserContext is not StreamDataQueryUserContext uc)
                throw AssetRepositoryException.UserContextNotSet();

            var gql = (GraphQlUserContext)ctx.UserContext;
            var tenantId = gql.TenantId;
            var streamDataRepo = gql.TenantContext.GetStreamDataRepository()
                ?? throw AssetRepositoryException.StreamDataNotAvailable();

            var loaded = uc.LoadedQuery;
            var ckTypeId = dto.AssociatedCkTypeId;

            var archiveSnapshot = await gql.TenantContext.GetCkArchiveRuntimeStore().GetAsync(uc.ArchiveRtId)
                ?? throw new ArchiveNotFoundException(uc.ArchiveRtId);
            var fieldResolver = new StreamDataFieldResolver(archiveSnapshot.Columns.Select(c => c.Path));
            var execOverride = ctx.GetArgument<StreamDataArguments?>(Statics.StreamDataArgument);

            StreamQueryExecutionInput input;
            IReadOnlyList<ColumnNameMapping> resolvedColumnNames;

            switch (loaded)
            {
                case RtSimpleSdQuery simple:
                {
                    var columnNames = simple.Columns?.ToList() ?? [];
                    var fieldFilterList = simple.FieldFilter?.ToList();

                    ctx.TryGetArgument(Statics.SortOrderArg, out IEnumerable<SortDto>? runtimeSortDtos);
                    var runtimeSortList = runtimeSortDtos?.ToList();
                    var persistedSortList = simple.Sorting?.ToList();

                    var sortFieldNames = runtimeSortList is { Count: > 0 }
                        ? runtimeSortList.Select(s => s.AttributePath)
                        : persistedSortList?.Select(s => s.AttributePath);

                    StreamDataFieldValidation.ValidateStreamDataFields(
                        fieldResolver, columnNames, sortFieldNames,
                        fieldFilterList is { Count: > 0 }
                            ? fieldFilterList.Where(f => f.ComparisonValue != null)
                                .Select(f => f.AttributePath)
                            : null);

                    resolvedColumnNames = fieldResolver.ResolveToMappings(columnNames);

                    IReadOnlyList<SortOrderItem>? sortOrders;
                    if (runtimeSortList is { Count: > 0 })
                    {
                        sortOrders = StreamDataGraphQlMapper.MapSortOrders(runtimeSortList);
                    }
                    else
                    {
                        sortOrders = StreamDataGraphQlMapper.MapCkSortOrders(
                            persistedSortList,
                            s => s.AttributePath,
                            s => s.SortOrder);
                    }

                    input = new StreamQueryExecutionInput
                    {
                        Variant = StreamQueryVariant.Simple,
                        ArchiveRtId = uc.ArchiveRtId,
                        CkTypeId = ckTypeId,
                        ColumnPaths = columnNames,
                        RtIds = simple.RtIds?.Select(id => new OctoObjectId(id)).ToList(),
                        From = execOverride?.From ?? simple.From,
                        To = execOverride?.To ?? simple.To,
                        Limit = execOverride?.Limit ?? (simple.Limit.HasValue ? (int)simple.Limit.Value : null),
                        SortOrders = sortOrders,
                        FieldFilters = StreamDataGraphQlMapper.MapCkFieldFilters(
                            fieldFilterList,
                            f => f.AttributePath,
                            f => f.Operator,
                            f => f.ComparisonValue),
                        Offset = ctx.GetOffset(),
                        PageSize = ctx.First
                    };
                    break;
                }

                case RtAggregationSdQuery aggregation:
                {
                    var aggregationColumns = aggregation.Columns?.ToList() ?? [];
                    var fieldFilterList = aggregation.FieldFilter?.ToList();

                    StreamDataFieldValidation.ValidateStreamDataFields(
                        fieldResolver,
                        aggregationColumns.Select(c => c.AttributePath),
                        null,
                        fieldFilterList is { Count: > 0 }
                            ? fieldFilterList.Where(f => f.ComparisonValue != null)
                                .Select(f => f.AttributePath)
                            : null);

                    resolvedColumnNames = fieldResolver.ResolveToMappings(
                        aggregationColumns.Select(c => c.AttributePath));

                    input = new StreamQueryExecutionInput
                    {
                        Variant = StreamQueryVariant.Aggregation,
                        ArchiveRtId = uc.ArchiveRtId,
                        CkTypeId = ckTypeId,
                        AggregationColumns = aggregationColumns
                            .Select(c => new AggregationColumn(
                                c.AttributePath,
                                StreamDataGraphQlMapper.MapCkAggregationType(c.AggregationType)))
                            .ToList(),
                        RtIds = aggregation.RtIds?.Select(id => new OctoObjectId(id)).ToList(),
                        From = execOverride?.From ?? aggregation.From,
                        To = execOverride?.To ?? aggregation.To,
                        FieldFilters = StreamDataGraphQlMapper.MapCkFieldFilters(
                            fieldFilterList,
                            f => f.AttributePath,
                            f => f.Operator,
                            f => f.ComparisonValue)
                    };
                    break;
                }

                case RtGroupingAggregationSdQuery grouping:
                {
                    var groupingColumns = grouping.GroupingColumns?.ToList() ?? [];
                    var aggregationColumns = grouping.Columns?.ToList() ?? [];
                    var fieldFilterList = grouping.FieldFilter?.ToList();

                    StreamDataFieldValidation.ValidateStreamDataFields(
                        fieldResolver,
                        groupingColumns.Concat(aggregationColumns.Select(c => c.AttributePath)),
                        null,
                        fieldFilterList is { Count: > 0 }
                            ? fieldFilterList.Where(f => f.ComparisonValue != null)
                                .Select(f => f.AttributePath)
                            : null);

                    resolvedColumnNames = fieldResolver
                        .ResolveToMappings(groupingColumns)
                        .Concat(fieldResolver.ResolveToMappings(
                            aggregationColumns.Select(c => c.AttributePath)))
                        .ToList();

                    input = new StreamQueryExecutionInput
                    {
                        Variant = StreamQueryVariant.GroupingAggregation,
                        ArchiveRtId = uc.ArchiveRtId,
                        CkTypeId = ckTypeId,
                        GroupByColumnPaths = groupingColumns,
                        AggregationColumns = aggregationColumns
                            .Select(c => new AggregationColumn(
                                c.AttributePath,
                                StreamDataGraphQlMapper.MapCkAggregationType(c.AggregationType)))
                            .ToList(),
                        RtIds = grouping.RtIds?.Select(id => new OctoObjectId(id)).ToList(),
                        From = execOverride?.From ?? grouping.From,
                        To = execOverride?.To ?? grouping.To,
                        FieldFilters = StreamDataGraphQlMapper.MapCkFieldFilters(
                            fieldFilterList,
                            f => f.AttributePath,
                            f => f.Operator,
                            f => f.ComparisonValue)
                    };
                    break;
                }

                case RtDownsamplingSdQuery downsampling:
                {
                    var aggregationColumns = downsampling.Columns?.ToList() ?? [];
                    var fieldFilterList = downsampling.FieldFilter?.ToList();

                    StreamDataFieldValidation.ValidateStreamDataFields(
                        fieldResolver,
                        aggregationColumns.Select(c => c.AttributePath),
                        null,
                        fieldFilterList is { Count: > 0 }
                            ? fieldFilterList.Where(f => f.ComparisonValue != null)
                                .Select(f => f.AttributePath)
                            : null);

                    // Timestamp first (canonical PascalCase, wire camelCase), then resolved aggregation columns
                    var inputs = new[] { Constants.Timestamp }
                        .Concat(aggregationColumns.Select(c => c.AttributePath));
                    resolvedColumnNames = fieldResolver.ResolveToMappings(inputs);

                    input = new StreamQueryExecutionInput
                    {
                        Variant = StreamQueryVariant.Downsampling,
                        ArchiveRtId = uc.ArchiveRtId,
                        CkTypeId = ckTypeId,
                        AggregationColumns = aggregationColumns
                            .Select(c => new AggregationColumn(
                                c.AttributePath,
                                StreamDataGraphQlMapper.MapCkAggregationType(c.AggregationType)))
                            .ToList(),
                        RtIds = downsampling.RtIds?.Select(id => new OctoObjectId(id)).ToList(),
                        From = execOverride?.From ?? downsampling.From,
                        To = execOverride?.To ?? downsampling.To,
                        Limit = execOverride?.Limit ?? (downsampling.Limit.HasValue
                            ? (int)downsampling.Limit.Value
                            : null),
                        FieldFilters = StreamDataGraphQlMapper.MapCkFieldFilters(
                            fieldFilterList,
                            f => f.AttributePath,
                            f => f.Operator,
                            f => f.ComparisonValue)
                    };
                    break;
                }

                default:
                    throw new InvalidOperationException(
                        $"Unsupported RtStreamDataQuery subtype: {loaded.GetType().Name}");
            }

            var result = await StreamDataVariantExecutor.ExecuteAsync(streamDataRepo, input);

            _logger.LogDebug("StreamDataQueryDtoType Rows: got {Count} rows, totalCount={Total}",
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
        IResolveConnectionContext<StreamDataQueryDto?> ctx)
    {
        try
        {
            _logger.LogDebug("StreamDataQueryDtoType: executing Aggregations sub-connection");

            if (ctx.Source is not { } dto)
                throw AssetRepositoryException.SourceNotSet();

            if (dto.UserContext is not StreamDataQueryUserContext uc)
                throw AssetRepositoryException.UserContextNotSet();

            var gql = (GraphQlUserContext)ctx.UserContext;
            var streamDataRepo = gql.TenantContext.GetStreamDataRepository()
                ?? throw AssetRepositoryException.StreamDataNotAvailable();

            var loaded = uc.LoadedQuery;
            var ckTypeId = dto.AssociatedCkTypeId;
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

            // Re-use the same field filters as the loaded query (same data-set semantics).
            var fieldFilters = StreamDataGraphQlMapper.MapCkFieldFilters(
                loaded.FieldFilter?.ToList(),
                f => f.AttributePath,
                f => f.Operator,
                f => f.ComparisonValue);

            var input = new StreamQueryExecutionInput
            {
                Variant = StreamQueryVariant.Aggregation,
                ArchiveRtId = uc.ArchiveRtId,
                CkTypeId = ckTypeId,
                AggregationColumns = aggColumns,
                RtIds = loaded.RtIds?.Select(id => new OctoObjectId(id)).ToList(),
                From = loaded.From,
                To = loaded.To,
                FieldFilters = fieldFilters
            };

            var result = await StreamDataVariantExecutor.ExecuteAsync(streamDataRepo, input);

            _logger.LogDebug("StreamDataQueryDtoType Aggregations: got {Count} agg rows",
                result.Rows.Count);

            // Project the single aggregation row into QueryAggregationResult.
            // The engine returns stat values keyed by "Count_Path", "Min_Path", etc.
            var countStats = StreamDataStatisticsHelper.BuildStats(aggInput.CountAttributePaths, result, "Count");
            var minStats   = StreamDataStatisticsHelper.BuildStats(aggInput.MinValueAttributePaths, result, "Min");
            var maxStats   = StreamDataStatisticsHelper.BuildStats(aggInput.MaxValueAttributePaths, result, "Max");
            var avgStats   = StreamDataStatisticsHelper.BuildStats(aggInput.AvgAttributePaths, result, "Avg");
            var sumStats   = StreamDataStatisticsHelper.BuildStats(aggInput.SumAttributePaths, result, "Sum");

            // TotalCount from the first aggregation row, or 0 if no rows
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

}
