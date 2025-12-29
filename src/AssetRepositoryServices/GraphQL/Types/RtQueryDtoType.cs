using AssetRepositoryServices.Resources;
using GraphQL;
using GraphQL.Builders;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using ZstdSharp.Unsafe;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     Implements the GraphQL type for <see cref="RtQueryDto" />.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class RtQueryDtoType : ObjectGraphType<RtQueryDto>
{
    private readonly ILogger<RtQueryDtoType> _logger;

    /// <summary>
    ///     Constructor
    /// </summary>
    public RtQueryDtoType(ILogger<RtQueryDtoType> logger)
    {
        _logger = logger;
        Name = "RtQuery";
        Description = AssetTexts.Graphql_RtQuery_Description;
        Field(d => d.QueryRtId, typeof(NonNullGraphType<OctoObjectIdType>));
        Field(d => d.AssociatedCkTypeId, typeof(NonNullGraphType<RtCkIdGraph<CkTypeId>>));
        Field(d => d.Columns, typeof(NonNullGraphType<ListGraphType<NonNullGraphType<RtQueryColumnType>>>));

        Connection<NonNullGraphType<RtQueryRowDtoType>>("Rows")
            .Argument<GlobalQueryOptionsDtoType>(Statics.OptionsArg,
                "Global options to apply to the query, for example to include archived items.")
            .Argument<OctoObjectIdType>(Statics.RtIdArg, "Returns the entity with the given rtId.")
            .Argument<ListGraphType<OctoObjectIdType>>(Statics.RtIdsArg,
                "Returns entities with the given rtIds.")
            .Argument<SearchFilterDtoType>(Statics.SearchFilterArg, "Filters items based on text search")
            .Argument<ResultAggregationInputDtoType>(Statics.AggregationsArg,
                AssetTexts.Graphql_Type_Filter_Aggregations_Description)
            .Argument<ListGraphType<SortDtoType>>(Statics.SortOrderArg, "Sort order for items")
            .Argument<NearGeospatialFilterDtoType>(Statics.GeoNearFilterArg,
                "Geospatial filter for items, that searches for items near a point")
            .Argument<ListGraphType<FieldFilterDtoType>>(Statics.FieldFilterArg,
                "Filters items based on field compare")
            .ResolveAsync(ResolveRtQueryRowsAsync);
        Connection<NonNullGraphType<QueryAggregationResultType>>("Aggregations")
            .Argument<NonNullGraphType<ResultAggregationInputDtoType>>(Statics.AggregationsArg,
                AssetTexts.Graphql_Type_Filter_Aggregations_Description)
            .ResolveAsync(ResolveRtQueryAggregationAsync);
    }

    private async Task<object?> ResolveRtQueryAggregationAsync(IResolveConnectionContext<RtQueryDto> context)
    {
        try
        {
            _logger.LogDebug("GraphQL query handling for runtime aggregation started");

            var ckCacheService = context.GetCkCacheService();
            var sessionAccessor = context.GetSessionAccessor();

            var graphQlUserContext = (GraphQlUserContext)context.UserContext;

            if (context.Source is not { } rtQueryDto)
            {
                throw AssetRepositoryException.SourceNotSet();
            }

            var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();

            var offset = context.GetOffset();
            var queryOptions = context.GetQueryOptions();

            var roleIdDirectionPairs = RtPathEvaluator.TokenizeAndGetNavigationPairsByRtCkId(ckCacheService,
                tenantRepository.TenantId, rtQueryDto.AssociatedCkTypeId,
                rtQueryDto.Columns.Select(column => column.AttributePath));

            var resultSet = await tenantRepository.GetRtEntitiesGraphByTypeAsync(sessionAccessor.Session,
                rtQueryDto.AssociatedCkTypeId, queryOptions, roleIdDirectionPairs, offset,
                context.First);

            if (resultSet.AggregationResult == null)
            {
                throw AssetRepositoryException.AggregationResultNull();
            }

            _logger.LogDebug("GraphQL query handling returning data");
            return ConnectionUtils.ToOctoConnection([
                    new QueryAggregationResult(
                        resultSet.TotalCount,
                        resultSet.AggregationResult.CountStatistics,
                        resultSet.AggregationResult.MinStatistics,
                        resultSet.AggregationResult.MaxStatistics,
                        resultSet.AggregationResult.AvgStatistics,
                        resultSet.AggregationResult.SumStatistics,
                        resultSet.FieldAggregationResult)
                ]
                , context,
                0, 1, resultSet.AggregationResult,
                resultSet.FieldAggregationResult);
        }
        catch (Exception e)
        {
            return context.HandleException(e);
        }
    }

    private async Task<object?> ResolveRtQueryRowsAsync(IResolveConnectionContext<RtQueryDto?> context)
    {
        try
        {
            _logger.LogDebug("GraphQL query handling for runtime rows started");

            var ckCacheService = context.GetCkCacheService();
            var sessionAccessor = context.GetSessionAccessor();
            var graphQlUserContext = (GraphQlUserContext)context.UserContext;

            if (context.Source is not { } rtQueryDto)
            {
                throw AssetRepositoryException.SourceNotSet();
            }

            if (rtQueryDto.UserContext is not QueryUserContext queryUserContext)
            {
                throw AssetRepositoryException.UserContextNotSet();
            }

            var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();

            var offset = context.GetOffset();
            // Use query options from the persistent query definition, then enhance with runtime options
            var queryOptions = context.GetQueryOptions(queryUserContext.QueryOptions);

            // For aggregation queries, paging is applied in-memory to the results, not server-side
            var isAggregationQuery = queryUserContext.RtPersistentQuery is RtAggregationRtQuery
                                     or RtGroupingAggregationRtQuery;

            if (queryUserContext.RtPersistentQuery is RtAggregationRtQuery)
            {
                var aggregateResult = queryOptions.AggregateResult();

                // Add aggregation definitions to query options
                foreach (var tuple in queryUserContext.CkTypeQueryColumns)
                {
                    AddAggregationToResult(aggregateResult, tuple);
                }
            }
            else if (queryUserContext.RtPersistentQuery is RtGroupingAggregationRtQuery)
            {
                if (queryUserContext.GroupByColumnPaths == null || queryUserContext.GroupByColumnPaths.Count == 0)
                {
                    throw AssetRepositoryException.GroupByColumnPathsRequired();
                }

                var aggregateFieldGroupBy = queryOptions.AggregateFieldGroupBy(queryUserContext.GroupByColumnPaths.ToArray());

                // Add aggregation definitions to query options
                foreach (var tuple in queryUserContext.CkTypeQueryColumns)
                {
                    AddAggregationToGroupBy(aggregateFieldGroupBy, tuple);
                }
            }

            var roleIdDirectionPairs = RtPathEvaluator.TokenizeAndGetNavigationPairsByRtCkId(ckCacheService,
                tenantRepository.TenantId, rtQueryDto.AssociatedCkTypeId,
                rtQueryDto.Columns.Select(column => column.AttributePath));

            // For aggregation queries, don't pass paging parameters to the database query
            var resultSet = await tenantRepository.GetRtEntitiesGraphByTypeAsync(sessionAccessor.Session,
                rtQueryDto.AssociatedCkTypeId, queryOptions, roleIdDirectionPairs,
                isAggregationQuery ? null : offset,
                isAggregationQuery ? null : context.First);


            if (queryUserContext.RtPersistentQuery is RtAggregationRtQuery aggregationRtQuery)
            {
                _logger.LogDebug("GraphQL query handling returning aggregation data");

                if (resultSet.AggregationResult == null)
                {
                    throw AssetRepositoryException.AggregationResultNull();
                }

                return ConnectionUtils.ToConnection(
                [
                    RtAggregationQueryRowDtoType.CreateRtQueryRowDto(tenantRepository.TenantId,
                        aggregationRtQuery.QueryCkTypeId, resultSet.AggregationResult,
                        queryUserContext.CkTypeQueryColumns)
                ], context, 0, 1);
            }

            if (queryUserContext.RtPersistentQuery is RtGroupingAggregationRtQuery groupingAggregationRtQuery)
            {
                _logger.LogDebug("GraphQL query handling returning grouping aggregation data");

                if (resultSet.FieldAggregationResult == null || !resultSet.FieldAggregationResult.Any())
                {
                    throw AssetRepositoryException.FieldAggregationResultNull();
                }

                var fieldAggregationResults = resultSet.FieldAggregationResult.ToList();
                var totalCount = fieldAggregationResults.Count;
                var currentOffset = offset.GetValueOrDefault(0);

                // Apply paging to field aggregation results
                var pagedResults = fieldAggregationResults
                    .Skip(currentOffset)
                    .Take(context.First ?? totalCount)
                    .Select(fieldAggResult =>
                        RtGroupingAggregationQueryRowDtoType.CreateRtQueryRowDto(tenantRepository.TenantId,
                            groupingAggregationRtQuery.QueryCkTypeId, fieldAggResult,
                            queryUserContext.CkTypeQueryColumns));

                return ConnectionUtils.ToConnection(pagedResults, context,
                    totalCount > 0 ? currentOffset : 0, totalCount);
            }

            _logger.LogDebug("GraphQL query handling returning data");
            return ConnectionUtils.ToConnection(
                resultSet.Items.Select((entity, _) =>
                    RtSimpleQueryRowDtoType.CreateRtQueryRowDto(tenantRepository.TenantId, entity,
                        queryUserContext.CkTypeQueryColumns)), context,
                resultSet.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSet.TotalCount);
        }
        catch (Exception e)
        {
            return context.HandleException(e);
        }
    }

    public static RtQueryDto CreateRtQueryDto(RtAggregationRtQuery rtAggregationRtQuery,
        IReadOnlyList<Tuple<CkTypeQueryColumn, AggregationTypesDto>> ckTypeQueryColumns)
    {
        var queryOptions = RtEntityQueryOptions.Create();
        if (rtAggregationRtQuery.FieldFilter != null)
        {
            foreach (var fieldFilter in rtAggregationRtQuery.FieldFilter)
            {
                queryOptions.FieldFilter(fieldFilter.AttributePath.ToPascalCase(),
                    (FieldFilterOperator)fieldFilter.Operator,
                    fieldFilter.ComparisonValue);
            }
        }

        var rtQueryDto = new RtQueryDto
        {
            QueryRtId = rtAggregationRtQuery.RtId,
            AssociatedCkTypeId = rtAggregationRtQuery.QueryCkTypeId,
            Columns = ckTypeQueryColumns.Select(c => RtQueryColumnType.CreateRtQueryColumnDto(c.Item1, c.Item2))
                .ToList(),
            UserContext = new QueryUserContext(rtAggregationRtQuery, queryOptions, ckTypeQueryColumns)
        };

        return rtQueryDto;
    }

    public static RtQueryDto CreateRtQueryDto(RtSimpleRtQuery rtQuery,
        IReadOnlyList<Tuple<CkTypeQueryColumn, AggregationTypesDto>> ckTypeQueryColumns)
    {
        var queryOptions = RtEntityQueryOptions.Create();
        if (rtQuery.FieldFilter != null)
        {
            foreach (var fieldFilter in rtQuery.FieldFilter)
            {
                queryOptions.FieldFilter(fieldFilter.AttributePath.ToPascalCase(),
                    (FieldFilterOperator)fieldFilter.Operator,
                    fieldFilter.ComparisonValue);
            }
        }

        if (rtQuery.Sorting != null)
        {
            foreach (var sort in rtQuery.Sorting)
            {
                queryOptions.SortOrder(sort.AttributePath.ToPascalCase(), (SortOrders)sort.SortOrder);
            }
        }

        var rtQueryDto = new RtQueryDto
        {
            QueryRtId = rtQuery.RtId,
            AssociatedCkTypeId = rtQuery.QueryCkTypeId,
            Columns = ckTypeQueryColumns
                .Select(c => RtQueryColumnType.CreateRtQueryColumnDto(c.Item1, c.Item2)).ToList(),
            UserContext = new QueryUserContext(rtQuery, queryOptions, ckTypeQueryColumns)
        };

        return rtQueryDto;
    }

    private static void AddAggregationToResult(AggregationInput aggregateResult,
        Tuple<CkTypeQueryColumn, AggregationTypesDto> tuple)
    {
        switch (tuple.Item2)
        {
            case AggregationTypesDto.Count:
                aggregateResult.CountAttributePaths(tuple.Item1.Path);
                break;
            case AggregationTypesDto.Minimum:
                aggregateResult.MinAttributePaths(tuple.Item1.Path);
                break;
            case AggregationTypesDto.Maximum:
                aggregateResult.MaxAttributePaths(tuple.Item1.Path);
                break;
            case AggregationTypesDto.Average:
                aggregateResult.AvgAttributePaths(tuple.Item1.Path);
                break;
            case AggregationTypesDto.Sum:
                aggregateResult.SumAttributePaths(tuple.Item1.Path);
                break;
            default:
                throw AssetRepositoryException.AggregationTypeUnknown(tuple.Item2);
        }
    }

    private static void AddAggregationToGroupBy(FieldAggregationInput aggregateFieldGroupBy,
        Tuple<CkTypeQueryColumn, AggregationTypesDto> tuple)
    {
        switch (tuple.Item2)
        {
            case AggregationTypesDto.Count:
                aggregateFieldGroupBy.CountAttributePaths(tuple.Item1.Path);
                break;
            case AggregationTypesDto.Minimum:
                aggregateFieldGroupBy.MinAttributePaths(tuple.Item1.Path);
                break;
            case AggregationTypesDto.Maximum:
                aggregateFieldGroupBy.MaxAttributePaths(tuple.Item1.Path);
                break;
            case AggregationTypesDto.Average:
                aggregateFieldGroupBy.AvgAttributePaths(tuple.Item1.Path);
                break;
            case AggregationTypesDto.Sum:
                aggregateFieldGroupBy.SumAttributePaths(tuple.Item1.Path);
                break;
            default:
                throw AssetRepositoryException.AggregationTypeUnknown(tuple.Item2);
        }
    }

    public static RtQueryDto CreateRtQueryDto(RtGroupingAggregationRtQuery rtGroupingAggregationRtQuery,
        IReadOnlyList<Tuple<CkTypeQueryColumn, AggregationTypesDto>> ckTypeQueryColumns,
        IReadOnlyList<string> groupByColumnPaths)
    {
        var queryOptions = RtEntityQueryOptions.Create();
        if (rtGroupingAggregationRtQuery.FieldFilter != null)
        {
            foreach (var fieldFilter in rtGroupingAggregationRtQuery.FieldFilter)
            {
                queryOptions.FieldFilter(fieldFilter.AttributePath.ToPascalCase(),
                    (FieldFilterOperator)fieldFilter.Operator,
                    fieldFilter.ComparisonValue);
            }
        }

        // Build columns list: groupBy columns first, then aggregation columns
        var columns = new List<RtQueryColumnDto>();
        columns.AddRange(groupByColumnPaths.Select(RtQueryColumnType.CreateGroupByColumnDto));
        columns.AddRange(ckTypeQueryColumns.Select(c => RtQueryColumnType.CreateRtQueryColumnDto(c.Item1, c.Item2)));

        var rtQueryDto = new RtQueryDto
        {
            QueryRtId = rtGroupingAggregationRtQuery.RtId,
            AssociatedCkTypeId = rtGroupingAggregationRtQuery.QueryCkTypeId,
            Columns = columns,
            UserContext = new QueryUserContext(rtGroupingAggregationRtQuery, queryOptions, ckTypeQueryColumns, groupByColumnPaths)
        };

        return rtQueryDto;
    }

    private class QueryUserContext(
        RtPersistentQuery rtPersistentQuery,
        RtEntityQueryOptions queryOptions,
        IReadOnlyList<Tuple<CkTypeQueryColumn, AggregationTypesDto>> ckTypeQueryColumns,
        IReadOnlyList<string>? groupByColumnPaths = null)
    {
        public RtPersistentQuery RtPersistentQuery { get; } = rtPersistentQuery;
        public RtEntityQueryOptions QueryOptions { get; } = queryOptions;

        public IReadOnlyList<Tuple<CkTypeQueryColumn, AggregationTypesDto>> CkTypeQueryColumns { get; } =
            ckTypeQueryColumns;

        public IReadOnlyList<string>? GroupByColumnPaths { get; } = groupByColumnPaths;
    }
}