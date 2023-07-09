using System.Collections.Generic;
using System.Linq;
using GraphQL.Builders;
using GraphQL.DataLoader;
using GraphQL.Types;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;
using Meshmakers.Octo.SystematizedData.Persistence;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using MongoDB.Bson;

#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

public class RtEntityAssociationType : ObjectGraphType
{
    private readonly IDataLoaderContextAccessor _dataLoaderAccessor;
    private readonly GraphDirections _graphDirection;
    private readonly string _originCkId;
    private readonly string _roleId;
    private readonly IOctoSessionAccessor _sessionAccessor;

    public RtEntityAssociationType(string name, string description, IGraphTypesCache entityDtoCache,
        IDataLoaderContextAccessor dataLoaderAccessor,
        IOctoSessionAccessor sessionAccessor,
        IEnumerable<RtEntityDtoType> rtEntityDtoTypes, string originCkId, string roleId,
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
            this.Connection<object, IGraphType, RtEntityDto>(entityDtoCache, rtEntityDtoType, rtEntityDtoType.Name)
                .AddMetadata(Statics.CkId, rtEntityDtoType.CkId)
                // .Metadata(Statics.RoleId, roleId)
                // .Metadata(Statics.GraphDirection, graphDirection)
                .Argument<OctoObjectIdType>(Statics.RtIdArg, "Returns the entity with the given rtId.")
                .Argument<ListGraphType<OctoObjectIdType>>(Statics.RtIdsArg,
                    "Returns entities with the given rtIds.")
                .Argument<SearchFilterDtoType>(Statics.SearchFilterArg, "Filters items based on text search")
                .Argument<ListGraphType<SortDtoType>>(Statics.SortOrderArg, "Sort order for items")
                .Argument<ListGraphType<FieldFilterDtoType>>(Statics.FieldFilterArg,
                    "Filters items based on field compare")
                .Argument<GroupByFilterDtoType>(Statics.GroupByArg, "Groups items based on attributes")
                .Resolve(ResolveRtEntitiesQuery);
        }
    }

    private object ResolveRtEntitiesQuery(IResolveConnectionContext<RtEntityDto> ctx)
    {
        var targetCkId = (string)ctx.FieldDefinition.Metadata[Statics.CkId];
        // var roleId = (string) ctx.FieldDefinition.Metadata[Statics.RoleId];
        // var graphDirections = (GraphDirections) ctx.FieldDefinition.Metadata[Statics.GraphDirection];

        var offset = ctx.GetOffset();
        var dataQueryOperation = ctx.GetDataQueryOperation();

        ctx.TryGetArgument(Statics.RtIdArg, out var _, out OctoObjectId? key);
        ctx.TryGetArgument(Statics.RtIdsArg, null, out var hasKeysDefined, out IEnumerable<OctoObjectId>? keys);
        var keysList = keys?.Select(x => x.ToObjectId()).ToList();
        if (key != null)
        {
            keysList ??= new List<ObjectId>();
            keysList.Add(key.Value.ToObjectId());
        }
        
        if (ctx.Source.RtId == null)
        {
            return ConnectionUtils.ToConnection(new RtEntityDto[] { }, ctx, 0, 0, null);
        }

        var graphQlContext = (GraphQLUserContext)ctx.UserContext;

        var loader = _dataLoaderAccessor.Context.GetOrAddBatchLoader<ObjectId, ResultSet<RtEntity>>(
            $"Get{_originCkId}_{targetCkId}_{_roleId}", async keys =>
                await graphQlContext.TenantContext.Repository.GetRtAssociationTargetsAsync(_sessionAccessor.Session,
                    keys, _originCkId, _roleId, targetCkId, _graphDirection, keysList, dataQueryOperation, offset, ctx.First));

        var dataLoaderResult = loader.LoadAsync(ctx.Source.RtId.Value.ToObjectId());

        return dataLoaderResult.Then(resultSet => ConnectionUtils.ToConnection(
            resultSet.Result.Select(RtEntityDtoType.CreateRtEntityDto), ctx,
            resultSet.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSet.TotalCount, resultSet.Grouping));
    }
}
