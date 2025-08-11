using AssetRepositoryServices.Resources;
using GraphQL;
using GraphQL.Builders;
using GraphQL.DataLoader;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     Implements a generic runtime entities type that can be used for generic access to entities
/// </summary>
internal sealed class RtEntityGenericDtoType : ObjectGraphType<RtEntityDto>
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public RtEntityGenericDtoType()
    {
        Name = "RtEntity";
        Description = "A runtime entity type of Octo";
        Field(d => d.RtId, type: typeof(OctoObjectIdType));
        Field(d => d.CkTypeId, type: typeof(CkIdTypeGraph<CkTypeId>));
        Field(x => x.RtCreationDateTime, true);
        Field(x => x.RtChangedDateTime, true);
        Field(x => x.RtWellKnownName, true);
        Field(x => x.RtVersion, true);

        Connection<RtEntityAttributeDtoType>("attributes")
            .Argument<ListGraphType<StringGraphType>>(Statics.AttributeNamesFilterArg, "Filter of attribute names")
            .Resolve(ResolveAttributes);
        Connection<RtEntityGenericDtoType>("Associations")
            .Argument<NonNullGraphType<StringGraphType>>(Statics.RoleIdArg, "The role id of the association.")
            .Argument<BooleanGraphType>(Statics.IncludeIndirectArg,
                "Include indirect associations, otherwise direct associations are returned.")
            .Argument<NonNullGraphType<GraphDirectionsDtoType>>(Statics.DirectionArg,
                "The direction of the association.")
            .Argument<NonNullGraphType<StringGraphType>>(Statics.CkIdArg,
                "The construction kit type with the given id.")
            .Argument<SearchFilterDtoType>(Statics.SearchFilterArg, "Filters items based on text search")
            .Argument<ResultAggregationInputDtoType>(Statics.AggregationsArg,
                AssetTexts.Graphql_Type_Filter_Aggregations_Description)
            .Argument<ListGraphType<SortDtoType>>(Statics.SortOrderArg, "Sort order for items")
            .Argument<ListGraphType<FieldFilterDtoType>>(Statics.FieldFilterArg,
                "Filters items based on field compare")
            .Resolve(ResolveGenericRtAssociationsQuery);
    }

    private object ResolveAttributes(IResolveConnectionContext<RtEntityDto> context)
    {
        var ckCacheService = context.RequestServices?.GetRequiredService<ICkCacheService>();
        if (ckCacheService == null)
        {
            throw AssetRepositoryException.ServiceNotRegistered(typeof(ICkCacheService));
        }

        var graphQlContext = (GraphQlUserContext)context.UserContext;


        var ckTypeGraph = ckCacheService.GetCkType(graphQlContext.TenantId, context.Source.CkTypeId);

        IEnumerable<CkTypeAttributeGraph> resultList;
        if (context.HasArgument(Statics.AttributeNamesFilterArg))
        {
            var filterAttributeNames = context.GetArgument<IEnumerable<string>>(Statics.AttributeNamesFilterArg);

            resultList =
                ckTypeGraph.AllAttributes.Values.Where(a =>
                    filterAttributeNames.Contains(a.AttributeName.ToCamelCase()));
        }
        else
        {
            resultList = ckTypeGraph.AllAttributes.Values;
        }

        return ConnectionUtils.ToConnection(
            resultList.Select(item => CreateRtEntityAttributeDto((RtEntity)context.Source.UserContext!, item)),
            context);
    }

    private object? ResolveGenericRtAssociationsQuery(IResolveConnectionContext<RtEntityDto> ctx)
    {
        var sessionAccessor = ctx.RequestServices?.GetRequiredService<IOctoSessionAccessor>();
        if (sessionAccessor?.Session == null)
        {
            throw AssetRepositoryException.SessionUnavailable();
        }

        var dataLoaderAccessor = ctx.RequestServices?.GetRequiredService<IDataLoaderContextAccessor>();
        if (dataLoaderAccessor?.Context == null)
        {
            throw AssetRepositoryException.DataLoaderContextUnavailable();
        }

        var graphQlUserContext = (GraphQlUserContext)ctx.UserContext;

        var offset = ctx.GetOffset();
        var dataQueryOperation = ctx.GetDataQueryOperation();

        if (!ctx.TryGetArgument(Statics.RoleIdArg, out string? roleId))
        {
            throw AssetRepositoryException.RoleIdMissing();
        }

        if (!ctx.TryGetArgument(Statics.IncludeIndirectArg, out bool? indirectAssociations))
        {
            indirectAssociations = false;
        }

        if (!ctx.TryGetArgument(Statics.DirectionArg, out GraphDirections? direction))
        {
            throw AssetRepositoryException.DirectionMissing();
        }

        if (!ctx.TryGetArgument(Statics.CkId, out string? ckIdObj))
        {
            ctx.Errors.Add(new ExecutionError("Invalid query. Missing construction kit id.")
                { Code = Statics.GraphQlErrorCommon });
            return null;
        }

        CkId<CkTypeId> targetCkId = new(ckIdObj);

        var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();

        if (indirectAssociations.Value)
        {
            var loader = dataLoaderAccessor.Context.GetOrAddBatchLoader<OctoObjectId, IResultSet<RtEntity>>(
                $"Get{ctx.Source.CkTypeId}_{targetCkId}_{roleId}_{direction.Value}", async rtIds =>
                    await tenantRepository.GetIndirectRtAssociationTargetsAsync(
                        sessionAccessor.Session, rtIds, ctx.Source.CkTypeId,
                        new CkId<CkAssociationRoleId>(roleId),
                        direction.Value,
                        null, targetCkId, dataQueryOperation, offset, ctx.First));

            var dataLoaderResult = loader.LoadAsync(ctx.Source.RtId);

            return dataLoaderResult.Then(resultSet => ConnectionUtils.ToConnection(
                resultSet.Items.Select(RtEntityDtoType.CreateRtEntityDto), ctx,
                resultSet.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSet.TotalCount));
        }
        else
        {
            var loader = dataLoaderAccessor.Context.GetOrAddBatchLoader<OctoObjectId, IResultSet<RtEntity>>(
                $"Get{ctx.Source.CkTypeId}_{targetCkId}_{roleId}_{direction.Value}", async rtIds =>
                    await tenantRepository.GetRtAssociationTargetsAsync(
                        sessionAccessor.Session, rtIds, ctx.Source.CkTypeId,
                        new CkId<CkAssociationRoleId>(roleId),
                        targetCkId, direction.Value,
                        null, dataQueryOperation, offset, ctx.First));

            var dataLoaderResult = loader.LoadAsync(ctx.Source.RtId);

            return dataLoaderResult.Then(resultSet => ConnectionUtils.ToConnection(
                resultSet.Items.Select(RtEntityDtoType.CreateRtEntityDto), ctx,
                resultSet.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSet.TotalCount));
        }
    }

    private RtEntityAttributeDto CreateRtEntityAttributeDto(RtEntity rtEntity,
        CkTypeAttributeGraph ckTypeAttributeGraph)
    {
        var attributeDto = new RtEntityAttributeDto
        {
            AttributeName = ckTypeAttributeGraph.AttributeName.ToCamelCase(),
            Value = rtEntity.GetAttributeValueOrDefault(ckTypeAttributeGraph.AttributeName)
        };
        return attributeDto;
    }
}