using AssetRepositoryServices.Resources;
using GraphQL;
using GraphQL.Builders;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using NLog;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     Implements the GraphQL type for <see cref="RtTransientQueryDto"/>.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class RtTransientQueryDtoType : ObjectGraphType<RtTransientQueryDto>
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    ///     Constructor
    /// </summary>
    public RtTransientQueryDtoType()
    {
        Name = "RtTransientQuery";
        Description = AssetTexts.Graphql_RtQuery_Description;
        Field(d => d.AssociatedCkTypeId, type: typeof(NonNullGraphType<CkIdTypeGraph<CkTypeId>>));
        Field(d => d.Columns, type: typeof(NonNullGraphType<ListGraphType<NonNullGraphType<RtQueryColumnType>>>));

        Connection<NonNullGraphType<RtQueryRowDtoType>>("Rows")
            .ResolveAsync(ResolveRtQueryRowsAsync);
    }

    private async Task<object?> ResolveRtQueryRowsAsync(IResolveConnectionContext<object?> context)
    {
        Logger.Debug("GraphQL query handling for runtime rows started");

        var ckCacheService = context.RequestServices?.GetRequiredService<ICkCacheService>();
        if (ckCacheService == null)
        {
            throw AssetRepositoryException.ServiceNotRegistered(typeof(ICkCacheService));
        }


        var sessionAccessor = context.RequestServices?.GetRequiredService<IOctoSessionAccessor>();
        if (sessionAccessor?.Session == null)
        {
            throw AssetRepositoryException.SessionUnavailable();
        }

        var graphQlUserContext = (GraphQlUserContext)context.UserContext;

        if (context.Source is not RtTransientQueryDto rtTransientQueryDto)
        {
            context.Errors.Add(new ExecutionError("Invalid query. Query not found.")
                { Code = Statics.GraphQlErrorCommon });
            return null;
        }

        if (rtTransientQueryDto.UserContext is not QueryUserContext queryUserContext)
        {
            context.Errors.Add(new ExecutionError("Invalid query. User context not found.")
                { Code = Statics.GraphQlErrorCommon });
            return null;
        }

        var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();

        var offset = context.GetOffset();

        var roleIdDirectionPairs = RtPathEvaluator.TokenizeAndGetNavigationPairs(ckCacheService,
            tenantRepository.TenantId, rtTransientQueryDto.AssociatedCkTypeId,
            rtTransientQueryDto.Columns.Select(column => column.AttributePath));

        try
        {
            var resultSet = await tenantRepository.GetRtEntitiesGraphByTypeAsync(sessionAccessor.Session,
                rtTransientQueryDto.AssociatedCkTypeId, queryUserContext.DataQueryOperation,
                roleIdDirectionPairs, offset, context.First);

            Logger.Debug("GraphQL query handling returning data");
            return ConnectionUtils.ToConnection(
                resultSet.Items.Select((entity, _) =>
                    RtQueryRowDtoType.CreateRtQueryRowDto(tenantRepository.TenantId, entity,
                        queryUserContext.CkTypeQueryColumns)), context,
                resultSet.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSet.TotalCount,
                resultSet.AggregationResult, resultSet.FieldAggregationResult);
        }
        catch (OperationFailedException e)
        {
            context.Errors.Add(new ExecutionError(e.Message)
                { Code = Statics.GraphQlErrorCommon });
            return null;
        }
    }

    public static RtTransientQueryDto CreateTransientRtQueryDto(CkId<CkTypeId> ckTypeId,
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