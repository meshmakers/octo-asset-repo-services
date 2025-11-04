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
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v1;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;

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
            var dataQueryOperation = context.GetDataQueryOperation();

            var roleIdDirectionPairs = RtPathEvaluator.TokenizeAndGetNavigationPairsByRtCkId(ckCacheService,
                tenantRepository.TenantId, rtQueryDto.AssociatedCkTypeId,
                rtQueryDto.Columns.Select(column => column.AttributePath));

            var resultSet = await tenantRepository.GetRtEntitiesGraphByTypeAsync(sessionAccessor.Session,
                rtQueryDto.AssociatedCkTypeId, dataQueryOperation, roleIdDirectionPairs, offset,
                context.First);

            if (resultSet.AggregationResult == null)
            {
                throw AssetRepositoryException.AggregationResultNull();
            }

            _logger.LogDebug("GraphQL query handling returning data");
            return ConnectionUtils.ToConnection([
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
            var dataQueryOperation = context.GetDataQueryOperation();

            var roleIdDirectionPairs = RtPathEvaluator.TokenizeAndGetNavigationPairsByRtCkId(ckCacheService,
                tenantRepository.TenantId, rtQueryDto.AssociatedCkTypeId,
                rtQueryDto.Columns.Select(column => column.AttributePath));

            var resultSet = await tenantRepository.GetRtEntitiesGraphByTypeAsync(sessionAccessor.Session,
                rtQueryDto.AssociatedCkTypeId, dataQueryOperation, roleIdDirectionPairs, offset,
                context.First);

            _logger.LogDebug("GraphQL query handling returning data");
            return ConnectionUtils.ToConnection(
                resultSet.Items.Select((entity, _) =>
                    RtQueryRowDtoType.CreateRtQueryRowDto(tenantRepository.TenantId, entity,
                        queryUserContext.CkTypeQueryColumns)), context,
                resultSet.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSet.TotalCount,
                resultSet.AggregationResult, resultSet.FieldAggregationResult);
        }
        catch (Exception e)
        {
            return context.HandleException(e);
        }
    }

    public static RtQueryDto CreateRtQueryDto(RtQuery rtQuery,
        IReadOnlyList<CkTypeQueryColumn> ckTypeQueryColumns)
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
            Columns = ckTypeQueryColumns.Select(RtQueryColumnType.CreateRtQueryColumnDto).ToList(),
            UserContext = new QueryUserContext(ckTypeQueryColumns)
        };

        return rtQueryDto;
    }

    private class QueryUserContext(IReadOnlyList<CkTypeQueryColumn> ckTypeQueryColumns)
    {
        public IReadOnlyList<CkTypeQueryColumn> CkTypeQueryColumns { get; } = ckTypeQueryColumns;
    }
}