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
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using NLog;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     Implements the GraphQL type for <see cref="RtQueryDto"/>.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class RtQueryDtoType: ObjectGraphType<RtQueryDto>
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    ///     Constructor
    /// </summary>
    public RtQueryDtoType()
    {
        Name = "RtQuery";
        Description = AssetTexts.Graphql_RtQuery_Description;
        Field(d => d.QueryRtId, type: typeof(NonNullGraphType<OctoObjectIdType>));
        Field(d => d.AssociatedCkTypeId, type: typeof(NonNullGraphType<CkIdTypeGraph<CkTypeId>>));
        Field(d => d.Columns, type: typeof(NonNullGraphType<ListGraphType<NonNullGraphType<RtQueryColumnType>>>));

        Connection<NonNullGraphType<RtQueryRowDtoType>>("Rows")
            .ResolveAsync(ResolveRtQueryRowsAsync);
    }
    
     private async Task<object?> ResolveRtQueryRowsAsync(IResolveConnectionContext<object?> arg)
    {
        Logger.Debug("GraphQL query handling for runtime rows started");

        var sessionAccessor = arg.RequestServices?.GetRequiredService<IOctoSessionAccessor>();
        if (sessionAccessor?.Session == null)
        {
            throw AssetRepositoryException.SessionUnavailable();
        }

        var graphQlUserContext = (GraphQlUserContext)arg.UserContext;
        
        if (arg.Source is not RtQueryDto rtQueryDto)
        {
            arg.Errors.Add(new ExecutionError("Invalid query. Query not found.")
                { Code = Statics.GraphQLErrorCommon });
            return null;
        }
        
        if (rtQueryDto.UserContext is not QueryUserContext queryUserContext)
        {
            arg.Errors.Add(new ExecutionError("Invalid query. User context not found.")
                { Code = Statics.GraphQLErrorCommon });
            return null;
        }

        var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();

        var offset = arg.GetOffset();

        var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync(sessionAccessor.Session,
            rtQueryDto.AssociatedCkTypeId, queryUserContext.DataQueryOperation, offset, arg.First);

        Logger.Debug("GraphQL query handling returning data");
        return ConnectionUtils.ToConnection(
            resultSet.Items.Select((entity, _) => RtQueryRowDtoType.CreateRtQueryRowDto(entity, queryUserContext.CkTypeQueryColumns)), arg,
            0, (int)resultSet.TotalCount, resultSet.Grouping);
    }

    public static RtQueryDto CreateRtQueryDto(ConstructionKit.Models.System.Generated.System.v1.RtQuery rtQuery, IReadOnlyList<CkTypeQueryColumn> ckTypeQueryColumns)
    {
        DataQueryOperation dataQueryOperation = DataQueryOperation.Create();
        if (rtQuery.FieldFilter != null)
        {
            foreach (var fieldFilter in rtQuery.FieldFilter)
            {
                dataQueryOperation.FieldFilter(fieldFilter.AttributePath.ToPascalCase(),
                    (FieldFilterOperator)fieldFilter.Operator,
                    fieldFilter.ComparisonValue);
            }
        }
        if (rtQuery.Sorting != null)
        {
            foreach (var sort in rtQuery.Sorting)
            {
                dataQueryOperation.SortOrder(sort.AttributePath.ToPascalCase(), (SortOrders)sort.SortOrder);
            }
        }

        var rtQueryDto = new RtQueryDto
        {
            QueryRtId = rtQuery.RtId,
            AssociatedCkTypeId = rtQuery.QueryCkTypeId,
            Columns = ckTypeQueryColumns.Select(RtQueryColumnType.CreateRtQueryColumnDto).ToList(),
            UserContext = new QueryUserContext(dataQueryOperation, ckTypeQueryColumns)
        };

        return rtQueryDto;
    }

    private class QueryUserContext(DataQueryOperation dataQueryOperation, IReadOnlyList<CkTypeQueryColumn> ckTypeQueryColumns)
    {
        public DataQueryOperation DataQueryOperation { get; } = dataQueryOperation;
        public IReadOnlyList<CkTypeQueryColumn> CkTypeQueryColumns { get; } = ckTypeQueryColumns;
    }
}