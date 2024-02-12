using GraphQL.Builders;
using GraphQL.DataLoader;
using GraphQL.Types;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

internal class RtEntityAssociationType : ObjectGraphType
{
    private readonly IDataLoaderContextAccessor _dataLoaderAccessor;
    private readonly GraphDirections _graphDirection;
    private readonly CkId<CkTypeId> _originCkId;
    private readonly CkId<CkAssociationRoleId> _roleId;
    private readonly IOctoSessionAccessor _sessionAccessor;

    public RtEntityAssociationType(string name, string description, IGraphTypesCache entityDtoCache,
        IDataLoaderContextAccessor dataLoaderAccessor,
        IOctoSessionAccessor sessionAccessor,
        IEnumerable<RtEntityDtoType> rtEntityDtoTypes, CkId<CkTypeId> originCkId, CkId<CkAssociationRoleId> roleId,
        GraphDirections graphDirection)
    {
        _dataLoaderAccessor = dataLoaderAccessor;
        _sessionAccessor = sessionAccessor;
        _originCkId = originCkId;
        _roleId = roleId;
        _graphDirection = graphDirection;
        ArgumentValidation.ValidateString(nameof(name), name);
        ArgumentValidation.ValidateString(nameof(description), description);

        Name = name;
        Description = description;

        foreach (var rtEntityDtoType in rtEntityDtoTypes)
        {
            this.Connection<object?, IGraphType, RtEntityDto>(entityDtoCache, rtEntityDtoType, rtEntityDtoType.Name)
                .AddMetadata(Statics.CkId, rtEntityDtoType.CkTypeId)
                // .Metadata(Statics.RoleId, roleId)
                // .Metadata(Statics.GraphDirection, graphDirection)
                .Argument<OctoObjectIdType>(Statics.RtIdArg, "Returns the entity with the given rtId.")
                .Argument<ListGraphType<OctoObjectIdType>>(Statics.RtIdsArg,
                    "Returns entities with the given rtIds.")
                .Argument<SearchFilterDtoType>(Statics.SearchFilterArg, "Filters items based on text search")
                .Argument<ListGraphType<SortDtoType>>(Statics.SortOrderArg, "Sort order for items")
                .Argument<ListGraphType<FieldFilterDtoType>>(Statics.FieldFilterArg,
                    "Filters items based on field compare")
                .Argument<FieldGroupByType>(Statics.GroupByArg, "Groups items based on attributes")
                .Resolve(ResolveRtEntitiesQuery);
        }
    }

    private object ResolveRtEntitiesQuery(IResolveConnectionContext<RtEntityDto> ctx)
    {
        var targetCkId = (string?)ctx.FieldDefinition.Metadata[Statics.CkId];
        if (string.IsNullOrWhiteSpace(targetCkId))
        {
            throw AssetRepositoryException.CkIdMetadataMissing();
        }

        var offset = ctx.GetOffset();
        var dataQueryOperation = ctx.GetDataQueryOperation();

        ctx.TryGetArgument(Statics.RtIdArg, out OctoObjectId? key);
        ctx.TryGetArgument(Statics.RtIdsArg, null, out IEnumerable<OctoObjectId>? keys);
        var keysList = keys?.Select(x => x).ToList();
        if (key != null)
        {
            keysList ??= new List<OctoObjectId>();
            keysList.Add(key.Value);
        }

        if (keys == null || _dataLoaderAccessor.Context == null)
        {
            return ConnectionUtils.ToConnection(new RtEntityDto[] { }, ctx, 0, 0, null);
        }

        var graphQlContext = (GraphQlUserContext)ctx.UserContext;
        var tenantRepository = graphQlContext.TenantContext.GetTenantRepository();
        var loader = _dataLoaderAccessor.Context.GetOrAddBatchLoader<OctoObjectId, IResultSet<RtEntity>>(
            $"Get{_originCkId}_{targetCkId}_{_roleId}", async rtIds =>
                await tenantRepository.GetRtAssociationTargetsAsync(_sessionAccessor.Session,
                    rtIds, _originCkId, _roleId, targetCkId, _graphDirection, keysList, dataQueryOperation, offset, ctx.First)
        );

        var dataLoaderResult = loader.LoadAsync(ctx.Source.RtId);

        return dataLoaderResult.Then(resultSet => ConnectionUtils.ToConnection(
            resultSet.Items.Select(RtEntityDtoType.CreateRtEntityDto), ctx,
            resultSet.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSet.TotalCount, null));
    }
}