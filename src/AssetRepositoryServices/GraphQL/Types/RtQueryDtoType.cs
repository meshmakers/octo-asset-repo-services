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
        
        if (rtQueryDto.UserContext is not ConstructionKit.Models.System.Generated.System.v1.RtQuery rtQuery)
        {
            arg.Errors.Add(new ExecutionError("Invalid query. Query not found.")
                { Code = Statics.GraphQLErrorCommon });
            return null;
        }

        var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();

        var offset = arg.GetOffset();
        DataQueryOperation dataQueryOperation = DataQueryOperation.Create();
        if (rtQuery.FieldFilter != null)
        {
            foreach (var fieldFilter in rtQuery.FieldFilter)
            {
                dataQueryOperation.FieldFilter(fieldFilter.AttributeName.ToPascalCase(),
                    (FieldFilterOperator)fieldFilter.Operator,
                    fieldFilter.ComparisonValue);
            }
        }
        if (rtQuery.Sorting != null)
        {
            foreach (var sort in rtQuery.Sorting)
            {
                dataQueryOperation.SortOrder(sort.AttributeName.ToPascalCase(), (SortOrders)sort.SortOrder);
            }
        }

        var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync(sessionAccessor.Session,
            rtQuery.QueryCkTypeId, dataQueryOperation, offset, arg.First);

        Logger.Debug("GraphQL query handling returning data");
        return ConnectionUtils.ToConnection(
            resultSet.Items.Select((entity, _) => RtQueryRowDtoType.CreateRtQueryRowDto(entity, rtQuery)), arg,
            0, (int)resultSet.TotalCount, resultSet.Grouping);
    }
     
    public static RtQueryDto CreateRtQueryDto(CkTypeGraph ckTypeGraph, ConstructionKit.Models.System.Generated.System.v1.RtQuery rtQuery)
    {
        var rtQueryDto = new RtQueryDto
        {
            QueryRtId = rtQuery.RtId,
            AssociatedCkTypeId = rtQuery.QueryCkTypeId,
            Columns = rtQuery.Columns.Select(c => RtQueryColumnType.CreateRtQueryColumnDto(ckTypeGraph, rtQuery, c)).ToList(),
            UserContext = rtQuery
        };

        return rtQueryDto;
    }
}