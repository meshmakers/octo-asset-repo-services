using AssetRepositoryServices.Resources;
using GraphQL;
using GraphQL.Builders;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     Implements a GraphQL runtime query row type for a runtime query
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class RtQueryRowDtoType : ObjectGraphType<RtQueryRowDto>
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public RtQueryRowDtoType()
    {
        Name = "RtQueryRow";
        Description = AssetTexts.Graphql_RtQueryRow_Description;
        Field(d => d.RtId, type: typeof(OctoObjectIdType));
        Field(d => d.CkTypeId, type: typeof(CkIdTypeGraph<CkTypeId>));
        Field(x => x.RtCreationDateTime, true);
        Field(x => x.RtChangedDateTime, true);
        Field(x => x.RtWellKnownName, true);
        Field(x => x.RtVersion, true);

        Connection<RtQueryCellDtoType>("cells")
            .Argument<ListGraphType<StringGraphType>>(Statics.AttributePathsFilterArg,
                AssetTexts.Graphql_Arguments_AttributePathsFilter_Description)
            .Resolve(ResolveCells);
    }

    private object ResolveCells(IResolveConnectionContext<RtQueryRowDto> context)
    {
        var ckCacheService = context.RequestServices?.GetRequiredService<ICkCacheService>();
        if (ckCacheService == null)
        {
            throw AssetRepositoryException.ServiceNotRegistered(typeof(ICkCacheService));
        }

        var graphQlContext = (GraphQlUserContext)context.UserContext;

        var rtQueryRowUserContext = (RtQueryRowUserContext)context.Source.UserContext!;
        var ckTypeGraph = ckCacheService.GetCkType(graphQlContext.TenantId, context.Source.CkTypeId);
        
        var selectedColumns = rtQueryRowUserContext.RtQuery.Columns.Select(c => c.ToPascalCase());
        var resultList = ckTypeGraph.AllAttributes.Values.Where(a => selectedColumns.Contains(a.AttributeName));

        if (context.HasArgument(Statics.AttributeNamesFilterArg))
        {
            var filterAttributeNames = context.GetArgument<IEnumerable<string>>(Statics.AttributeNamesFilterArg);

            resultList =
                resultList.Where(a =>
                    filterAttributeNames.Contains(a.AttributeName.ToCamelCase()));
        }
     

        return ConnectionUtils.ToConnection(
            resultList.Select(item => CreateRtQueryCellDto(rtQueryRowUserContext.RtEntity, item)),
            context, null);
    }

    private RtQueryCellDto CreateRtQueryCellDto(RtEntity rtEntity,
        CkTypeAttributeGraph ckTypeAttributeGraph)
    {
        var cellDto = new RtQueryCellDto
        {
            AttributePath = ckTypeAttributeGraph.AttributeName.ToCamelCase(),
            Value = rtEntity.GetAttributeValueOrDefault(ckTypeAttributeGraph.AttributeName)
        };
        return cellDto;
    }

    public static RtQueryRowDto CreateRtQueryRowDto(RtEntity rtEntity, ConstructionKit.Models.System.Generated.System.v1.RtQuery rtQuery)
    {
        var rtQueryRowDto = new RtQueryRowDto
        {
            RtId = rtEntity.RtId,
            CkTypeId = rtEntity.CkTypeId ?? throw OctoGraphQLException.CkTypeIdUndefined(),
            RtCreationDateTime = rtEntity.RtCreationDateTime,
            RtChangedDateTime = rtEntity.RtChangedDateTime,
            RtWellKnownName = rtEntity.RtWellKnownName,
            RtVersion = rtEntity.RtVersion,
            UserContext = new RtQueryRowUserContext(rtEntity, rtQuery)
        };

        return rtQueryRowDto;
    }
}

internal class RtQueryRowUserContext(RtEntity rtEntity, ConstructionKit.Models.System.Generated.System.v1.RtQuery rtQuery)
{
    public ConstructionKit.Models.System.Generated.System.v1.RtQuery RtQuery { get; } = rtQuery;

    public RtEntity RtEntity { get; } = rtEntity;
}