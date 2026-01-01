using AssetRepositoryServices.Resources;
using GraphQL.Builders;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     Implements the GraphQL type for <see cref="RtTransientQueryDto" />.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class RtTransientQueryDtoType : ObjectGraphType<RtTransientQueryDto>
{
    private readonly ILogger<RtTransientQueryDtoType> _logger;

    /// <summary>
    ///     Constructor
    /// </summary>
    public RtTransientQueryDtoType(ILogger<RtTransientQueryDtoType> logger)
    {
        _logger = logger;
        Name = "RtTransientQuery";
        Description = AssetTexts.Graphql_RtQuery_Description;
        Field(d => d.AssociatedCkTypeId, typeof(NonNullGraphType<RtCkIdGraph<CkTypeId>>));
        Field(d => d.Columns, typeof(NonNullGraphType<ListGraphType<NonNullGraphType<RtQueryColumnType>>>));

        Connection<NonNullGraphType<QueryAggregationResultType>>("Aggregations")
            .Argument<NonNullGraphType<ResultAggregationInputDtoType>>(Statics.AggregationsArg,
                AssetTexts.Graphql_Type_Filter_Aggregations_Description)
            .ResolveAsync(ResolveRtQueryAggregationAsync);
        Connection<NonNullGraphType<RtQueryRowDtoType>>("Rows")
            .ResolveAsync(ResolveRtQueryRowsAsync);
    }

    private async Task<object?> ResolveRtQueryAggregationAsync(IResolveConnectionContext<RtTransientQueryDto?> context)
    {
        try
        {
            _logger.LogDebug("GraphQL query handling for runtime aggregation started");
            var ckCacheService = context.GetCkCacheService();
            var sessionAccessor = context.GetSessionAccessor();

            var graphQlUserContext = (GraphQlUserContext)context.UserContext;

            if (context.Source is not { } rtTransientQueryDto)
            {
                throw AssetRepositoryException.SourceNotSet();
            }

            if (rtTransientQueryDto.UserContext is not QueryUserContext queryUserContext)
            {
                throw AssetRepositoryException.UserContextNotSet();
            }

            var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();

            var offset = context.GetOffset();
            var queryOptions = context.GetQueryOptions(queryUserContext.QueryOptions);

            var roleIdDirectionPairs = RtPathEvaluator.TokenizeAndGetNavigationPairsByRtCkId(ckCacheService,
                tenantRepository.TenantId, rtTransientQueryDto.AssociatedCkTypeId,
                rtTransientQueryDto.Columns.Select(column => column.AttributePath));

            var resultSet = await tenantRepository.GetRtEntitiesGraphByTypeAsync(sessionAccessor.Session,
                rtTransientQueryDto.AssociatedCkTypeId, queryOptions,
                roleIdDirectionPairs, offset, context.First);

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

    private async Task<object?> ResolveRtQueryRowsAsync(IResolveConnectionContext<RtTransientQueryDto?> context)
    {
        try
        {
            _logger.LogDebug("GraphQL query handling for runtime rows started");

            var ckCacheService = context.GetCkCacheService();
            var sessionAccessor = context.GetSessionAccessor();

            var graphQlUserContext = (GraphQlUserContext)context.UserContext;

            if (context.Source is not { } rtTransientQueryDto)
            {
                throw AssetRepositoryException.SourceNotSet();
            }

            if (rtTransientQueryDto.UserContext is not QueryUserContext queryUserContext)
            {
                throw AssetRepositoryException.UserContextNotSet();
            }

            var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();

            var offset = context.GetOffset();
            var first = context.First;

            if (queryUserContext.QueryType == QueryType.Aggregation)
            {
                offset = null;
                first = null;
                var aggregateResult = queryUserContext.QueryOptions.AggregateResult();

                // Add aggregation definitions to query options
                foreach (var tuple in queryUserContext.CkTypeQueryColumns)
                {
                    AddAggregationToResult(aggregateResult, tuple);
                }
            }
            else if (queryUserContext.QueryType == QueryType.GroupingAggregation)
            {
                offset = null;
                first = null;
                if (queryUserContext.GroupByColumnPaths == null)
                {
                    throw AssetRepositoryException.GroupByColumnPathsRequired();
                }

                var aggregateFieldGroupBy = queryUserContext.QueryOptions.AggregateFieldGroupBy(queryUserContext.GroupByColumnPaths.ToArray());

                // Add aggregation definitions to query options
                foreach (var tuple in queryUserContext.CkTypeQueryColumns)
                {
                    AddAggregationToGroupBy(aggregateFieldGroupBy, tuple);
                }
            }

            var roleIdDirectionPairs = RtPathEvaluator.TokenizeAndGetNavigationPairsByRtCkId(ckCacheService,
                tenantRepository.TenantId, rtTransientQueryDto.AssociatedCkTypeId,
                rtTransientQueryDto.Columns.Select(column => column.AttributePath));

            var resultSet = await tenantRepository.GetRtEntitiesGraphByTypeAsync(sessionAccessor.Session,
                rtTransientQueryDto.AssociatedCkTypeId, queryUserContext.QueryOptions,
                roleIdDirectionPairs, offset, first);

            if (queryUserContext.QueryType == QueryType.Aggregation)
            {
                _logger.LogDebug("GraphQL query handling returning aggregation data");

                if (resultSet.AggregationResult == null)
                {
                    throw AssetRepositoryException.AggregationResultNull();
                }

                return ConnectionUtils.ToConnection(
                [
                    RtAggregationQueryRowDtoType.CreateRtQueryRowDto(tenantRepository.TenantId,
                        rtTransientQueryDto.AssociatedCkTypeId, resultSet.AggregationResult,
                        queryUserContext.CkTypeQueryColumns)
                ], context, 0, 1);
            }

            if (queryUserContext.QueryType == QueryType.GroupingAggregation)
            {
                _logger.LogDebug("GraphQL query handling returning grouping aggregation data");

                if (resultSet.FieldAggregationResult == null)
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
                            rtTransientQueryDto.AssociatedCkTypeId, fieldAggResult,
                            queryUserContext.CkTypeQueryColumns));

                return ConnectionUtils.ToConnection(pagedResults, context,
                    totalCount > 0 ? currentOffset : 0, totalCount);
            }

            _logger.LogDebug("GraphQL query handling returning data");
            return ConnectionUtils.ToOctoConnection(
                resultSet.Items.Select((entity, _) =>
                    RtSimpleQueryRowDtoType.CreateRtQueryRowDto(tenantRepository.TenantId, entity,
                        queryUserContext.CkTypeQueryColumns)), context,
                resultSet.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSet.TotalCount,
                resultSet.AggregationResult, resultSet.FieldAggregationResult);
        }
        catch (Exception e)
        {
            return context.HandleException(e);
        }
    }

    public static RtTransientQueryDto CreateTransientRtQueryDto(QueryType queryType, RtCkId<CkTypeId> ckTypeId,
        RtEntityQueryOptions queryOptions, IReadOnlyList<Tuple<CkTypeQueryColumn, AggregationTypesDto>> ckTypeQueryColumns,
        IReadOnlyList<string>? groupByColumnPaths = null)
    {
        var columns = new List<RtQueryColumnDto>();

        // For grouping aggregation, add the groupBy columns first
        if (queryType == QueryType.GroupingAggregation && groupByColumnPaths != null)
        {
            columns.AddRange(groupByColumnPaths.Select(RtQueryColumnType.CreateGroupByColumnDto));
        }

        // Then add the aggregation columns
        columns.AddRange(ckTypeQueryColumns.Select(c => RtQueryColumnType.CreateRtQueryColumnDto(c.Item1, c.Item2)));

        var rtTransientQueryDto = new RtTransientQueryDto
        {
            AssociatedCkTypeId = ckTypeId,
            Columns = columns,
            UserContext = new QueryUserContext(queryType, queryOptions, ckTypeQueryColumns, groupByColumnPaths)
        };

        return rtTransientQueryDto;
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

    private class QueryUserContext(
        QueryType queryType,
        RtEntityQueryOptions queryOptions,
        IReadOnlyList<Tuple<CkTypeQueryColumn, AggregationTypesDto>> ckTypeQueryColumns,
        IReadOnlyList<string>? groupByColumnPaths = null)
    {
        public QueryType QueryType { get; } = queryType;
        public RtEntityQueryOptions QueryOptions { get; } = queryOptions;
        public IReadOnlyList<Tuple<CkTypeQueryColumn, AggregationTypesDto>> CkTypeQueryColumns { get; } = ckTypeQueryColumns;
        public IReadOnlyList<string>? GroupByColumnPaths { get; } = groupByColumnPaths;
    }

    internal enum QueryType
    {
        Standard,
        Aggregation,
        GroupingAggregation
    }
}