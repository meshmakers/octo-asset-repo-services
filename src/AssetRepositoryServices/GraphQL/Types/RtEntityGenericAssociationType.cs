using AssetRepositoryServices.Resources;
using GraphQL;
using GraphQL.Builders;
using GraphQL.DataLoader;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     Represents a GraphQL type for a runtime entity generic association in Octo.
/// </summary>
public sealed class RtEntityGenericAssociationType : ObjectGraphType<RtEntityGenericAssociation>
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="RtEntityGenericAssociationType" /> class
    /// </summary>
    public RtEntityGenericAssociationType()
    {
        Name = "RtEntityGenericAssociation";
        Description = "A runtime entity generic association type of OctoMesh";

        Connection<RtEntityGenericDtoType>("targets")
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
            .Resolve(ResolveGenericRtAssociationTargetsQuery);

        Connection<RtAssociationDtoType>("definitions")
            .Argument<StringGraphType>(Statics.RoleIdArg, "The role id of the association.")
            .Argument<NonNullGraphType<GraphDirectionsDtoType>>(Statics.DirectionArg,
                "The direction of the association.")
            .Argument<RtCkIdGraph<CkTypeId>>(Statics.RelatedRtCkId,
                "A construction kit type id to filter the associations by target type.")
            .Argument<OctoObjectIdType>(Statics.RelatedRtId,
                "A construction kit object id to filter the associations by target runtime entity.")
            .Resolve(ResolveGenericRtAssociationsQuery);
    }

    private object ResolveGenericRtAssociationsQuery(IResolveConnectionContext<RtEntityGenericAssociation> ctx)
    {
        var sessionAccessor = ctx.GetSessionAccessor();

        var dataLoaderAccessor = ctx.RequestServices?.GetRequiredService<IDataLoaderContextAccessor>();
        if (dataLoaderAccessor?.Context == null)
        {
            throw AssetRepositoryException.DataLoaderContextUnavailable();
        }

        var direction = ctx.GetArgument<GraphDirections>(Statics.DirectionArg);
        var ckAssociationRoleId = ctx.GetArgument<RtCkId<CkAssociationRoleId>?>(Statics.RoleIdArg);
        var relatedRtCkTypeId = ctx.GetArgument<RtCkId<CkTypeId>?>(Statics.RelatedRtCkId);
        var relatedRtId = ctx.GetArgument<OctoObjectId?>(Statics.RelatedRtId);

        var graphQlUserContext = (GraphQlUserContext)ctx.UserContext;
        var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();
        var offset = ctx.GetOffset();

        var queryOptions =
            RtAssociationExtendedQueryOptions.Create(direction, ckAssociationRoleId, relatedRtCkTypeId,
                relatedRtId, offset, ctx.First);

        var loader = dataLoaderAccessor.Context.GetOrAddBatchLoader<RtEntityId, IResultSet<RtAssociation>>(
            $"Get{ctx.Source.RtEntityDto.CkTypeId}_{direction}", async rtEntityIds =>
                await tenantRepository.GetRtAssociationsAsync(sessionAccessor.Session, rtEntityIds, queryOptions));
        var dataLoaderResult = loader.LoadAsync(ctx.Source.RtEntityDto.ToRtEntityId());

        return dataLoaderResult.Then(resultSet => ConnectionUtils.ToOctoConnection(
            resultSet.Items.Select(RtAssociationDtoType.CreateRtAssociationDto), ctx,
            resultSet.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSet.TotalCount));
    }

    private object ResolveGenericRtAssociationTargetsQuery(IResolveConnectionContext<RtEntityGenericAssociation> ctx)
    {
        var sessionAccessor = ctx.GetSessionAccessor();

        var dataLoaderAccessor = ctx.RequestServices?.GetRequiredService<IDataLoaderContextAccessor>();
        if (dataLoaderAccessor?.Context == null)
        {
            throw AssetRepositoryException.DataLoaderContextUnavailable();
        }

        var graphQlUserContext = (GraphQlUserContext)ctx.UserContext;

        var offset = ctx.GetOffset();
        var queryOptions = ctx.GetQueryOptions();

        if (!ctx.TryGetArgument(Statics.IncludeIndirectArg, out bool? indirectAssociations))
        {
            indirectAssociations = false;
        }

        var roleId = ctx.GetArgument<string>(Statics.RoleIdArg);
        var direction = ctx.GetArgument<GraphDirections>(Statics.DirectionArg);
        var targetCkId = ctx.GetArgument<RtCkId<CkTypeId>>(Statics.CkId);

        var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();

        if (indirectAssociations.Value)
        {
            var loader = dataLoaderAccessor.Context.GetOrAddBatchLoader<RtEntityId, IResultSet<RtEntity>>(
                $"Get{ctx.Source.RtEntityDto.CkTypeId}_{targetCkId}_{roleId}_{direction}", async rtEntityIds =>
                    await tenantRepository.GetIndirectRtAssociationTargetsAsync(
                        sessionAccessor.Session, rtEntityIds.Select(x => x.RtId), ctx.Source.RtEntityDto.CkTypeId,
                        new RtCkId<CkAssociationRoleId>(roleId),
                        direction,
                        null, targetCkId, queryOptions, offset, ctx.First));

            var dataLoaderResult = loader.LoadAsync(ctx.Source.RtEntityDto.ToRtEntityId());

            return dataLoaderResult.Then(resultSet => ConnectionUtils.ToOctoConnection(
                resultSet.Items.Select(RtEntityDtoType.CreateRtEntityDto), ctx,
                resultSet.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSet.TotalCount));
        }
        else
        {
            var loader = dataLoaderAccessor.Context.GetOrAddBatchLoader<RtEntityId, IResultSet<RtEntity>>(
                $"Get{ctx.Source.RtEntityDto.CkTypeId}_{targetCkId}_{roleId}_{direction}", async rtEntityIds =>
                    await tenantRepository.GetRtAssociationTargetsAsync(
                        sessionAccessor.Session, rtEntityIds.Select(x => x.RtId), ctx.Source.RtEntityDto.CkTypeId,
                        new RtCkId<CkAssociationRoleId>(roleId),
                        targetCkId, direction,
                        null, queryOptions, offset, ctx.First));

            var dataLoaderResult = loader.LoadAsync(ctx.Source.RtEntityDto.ToRtEntityId());

            return dataLoaderResult.Then(resultSet => ConnectionUtils.ToOctoConnection(
                resultSet.Items.Select(RtEntityDtoType.CreateRtEntityDto), ctx,
                resultSet.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSet.TotalCount));
        }
    }
}