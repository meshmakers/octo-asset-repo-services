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
        Field(d => d.AssociatedCkTypeId, typeof(NonNullGraphType<CkIdGraph<CkTypeId>>));
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
            var dataQueryOperation = context.GetDataQueryOperation(queryUserContext.DataQueryOperation);

            var roleIdDirectionPairs = RtPathEvaluator.TokenizeAndGetNavigationPairsByRtCkId(ckCacheService,
                tenantRepository.TenantId, rtTransientQueryDto.AssociatedCkTypeId,
                rtTransientQueryDto.Columns.Select(column => column.AttributePath));

            var resultSet = await tenantRepository.GetRtEntitiesGraphByTypeAsync(sessionAccessor.Session,
                rtTransientQueryDto.AssociatedCkTypeId, dataQueryOperation,
                roleIdDirectionPairs, offset, context.First);

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

            var roleIdDirectionPairs = RtPathEvaluator.TokenizeAndGetNavigationPairsByRtCkId(ckCacheService,
                tenantRepository.TenantId, rtTransientQueryDto.AssociatedCkTypeId,
                rtTransientQueryDto.Columns.Select(column => column.AttributePath));


            var resultSet = await tenantRepository.GetRtEntitiesGraphByTypeAsync(sessionAccessor.Session,
                rtTransientQueryDto.AssociatedCkTypeId, queryUserContext.DataQueryOperation,
                roleIdDirectionPairs, offset, context.First);

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

    public static RtTransientQueryDto CreateTransientRtQueryDto(RtCkId<CkTypeId> ckTypeId,
        DataQueryOperation dataQueryOperation, IReadOnlyList<CkTypeQueryColumn> ckTypeQueryColumns)
    {
        var rtTransientQueryDto = new RtTransientQueryDto
        {
            AssociatedCkTypeId = ckTypeId,
            Columns = ckTypeQueryColumns.Select(RtQueryColumnType.CreateRtQueryColumnDto).ToList(),
            UserContext = new QueryUserContext(dataQueryOperation, ckTypeQueryColumns)
        };

        return rtTransientQueryDto;
    }

    private class QueryUserContext(
        DataQueryOperation dataQueryOperation,
        IReadOnlyList<CkTypeQueryColumn> ckTypeQueryColumns)
    {
        public DataQueryOperation DataQueryOperation { get; } = dataQueryOperation;
        public IReadOnlyList<CkTypeQueryColumn> CkTypeQueryColumns { get; } = ckTypeQueryColumns;
    }
}