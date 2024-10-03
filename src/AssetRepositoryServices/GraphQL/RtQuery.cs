using GraphQL;
using GraphQL.Builders;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using NLog;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

[DoNotRegister]
internal sealed class RtQuery : ObjectGraphType
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public RtQuery(IGraphTypesCache graphTypesCache)
    {
        Name = "RuntimeModelQuery";

        Connection<RtEntityGenericDtoType>("RuntimeEntities")
            .Argument<StringGraphType>(Statics.CkIdArg, "The construction kit type with the given id.")
            .Argument<OctoObjectIdType>(Statics.RtIdArg, "Returns the entity with the given rtId.")
            .Argument<ListGraphType<OctoObjectIdType>>(Statics.RtIdsArg,
                "Returns entities with the given rtIds.")
            .Argument<SearchFilterDtoType>(Statics.SearchFilterArg, "Filters items based on text search")
            .Argument<FieldGroupByType>(Statics.GroupByArg, "Groups items based on attributes")
            .Argument<ListGraphType<SortDtoType>>(Statics.SortOrderArg, "Sort order for items")
            .Argument<ListGraphType<FieldFilterDtoType>>(Statics.FieldFilterArg,
                "Filters items based on field compare")
            .ResolveAsync(ResolveGenericRtEntitiesQuery);


        Connection<RtEntityGenericDtoType>("RuntimeQuery")
            .Argument<OctoObjectIdType>(Statics.RtIdArg, "The query runtime id.")
            .ResolveAsync(ResolveRtQueryAsync);


        foreach (var rtEntityDtoType in graphTypesCache.GetTypes())
        {
            this.Connection<object?, IGraphType, RtEntityDto>(graphTypesCache, rtEntityDtoType, rtEntityDtoType.Name)
                .AddMetadata(Statics.CkId, rtEntityDtoType.CkTypeId)
                .Argument<OctoObjectIdType>(Statics.RtIdArg, "Returns the entity with the given rtId.")
                .Argument<ListGraphType<OctoObjectIdType>>(Statics.RtIdsArg,
                    "Returns entities with the given rtIds.")
                .Argument<SearchFilterDtoType>(Statics.SearchFilterArg, "Filters items based on text search")
                .Argument<FieldGroupByType>(Statics.GroupByArg, "Groups items based on attributes")
                .Argument<ListGraphType<SortDtoType>>(Statics.SortOrderArg, "Sort order for items")
                .Argument<NearGeospatialFilterDtoType>(Statics.GeoNearFilterArg,
                    "Geospatial filter for items, that searches for items near a point")
                .Argument<ListGraphType<FieldFilterDtoType>>(Statics.FieldFilterArg,
                    "Filters items based on field compare")
                .ResolveAsync(ResolveRtEntitiesQuery);
        }
    }

    private async Task<object?> ResolveRtQueryAsync(IResolveConnectionContext<object?> arg)
    {
        Logger.Debug("GraphQL query handling for runtime query started");

        var sessionAccessor = arg.RequestServices?.GetRequiredService<IOctoSessionAccessor>();
        if (sessionAccessor?.Session == null)
        {
            throw AssetRepositoryException.SessionUnavailable();
        }

        var graphQlUserContext = (GraphQlUserContext)arg.UserContext;
        if (!arg.TryGetArgument(Statics.RtIdArg, out OctoObjectId? queryRtId))
        {
            arg.Errors.Add(new ExecutionError("Invalid query. Missing runtime id.")
                { Code = Statics.GraphQLErrorCommon });
            return null;
        }

        var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();
        var rtQuery =
            await tenantRepository.GetRtEntityByRtIdAsync<ConstructionKit.Models.System.Generated.System.v1.RtQuery>(
                sessionAccessor.Session, queryRtId.Value);

        if (rtQuery == null)
        {
            arg.Errors.Add(new ExecutionError("Invalid query. Query not found.")
                { Code = Statics.GraphQLErrorCommon });
            return null;
        }
        
        var offset = arg.GetOffset();
        DataQueryOperation dataQueryOperation = DataQueryOperation.Create();

        var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync(sessionAccessor.Session,
            rtQuery.QueryCkTypeId, dataQueryOperation, offset, arg.First);
        
        Logger.Debug("GraphQL query handling returning data");
        return ConnectionUtils.ToConnection(resultSet.Items.Select(RtEntityDtoType.CreateRtEntityDto), arg,
            0, (int)resultSet.TotalCount, resultSet.Grouping);
    }

    private async Task<object?> ResolveGenericRtEntitiesQuery(IResolveConnectionContext<object?> arg)
    {
        Logger.Debug("GraphQL query handling for generic runtime entity started");

        var sessionAccessor = arg.RequestServices?.GetRequiredService<IOctoSessionAccessor>();
        if (sessionAccessor?.Session == null)
        {
            throw AssetRepositoryException.SessionUnavailable();
        }

        var graphQlUserContext = (GraphQlUserContext)arg.UserContext;
        if (!arg.TryGetArgument(Statics.CkId, out string? ckIdObj))
        {
            arg.Errors.Add(new ExecutionError("Invalid query. Missing construction kit id.")
                { Code = Statics.GraphQLErrorCommon });
            return null;
        }

        CkId<CkTypeId> ckTypeId = new(ckIdObj);

        var offset = arg.GetOffset();
        var dataQueryOperation = arg.GetDataQueryOperation();

        var keysList = new List<OctoObjectId>();
        if (arg.TryGetArgument(Statics.RtIdArg, out OctoObjectId? rtId))
        {
            keysList.Add(rtId.Value);
        }

        if (arg.TryGetArgument(Statics.RtIdsArg, null, out IEnumerable<OctoObjectId>? rtIds))
        {
            keysList.AddRange(rtIds);
        }

        // if argument defined, but empty array, do not return any data. That mus be a mistake by client (otherwise
        // all entities are returned.
        if (!keysList.Any() && (arg.HasArgument(Statics.RtIdArg) || arg.HasArgument(Statics.RtIdsArg)))
        {
            return ConnectionUtils.ToConnection(new List<RtEntityDto>(), arg, null);
        }

        var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();
        if (keysList.Any())
        {
            var resultSetIds = await tenantRepository.GetRtEntitiesByIdAsync(
                sessionAccessor.Session, ckTypeId, keysList, dataQueryOperation,
                offset, arg.First);

            Logger.Debug("GraphQL query handling returning data by keys");
            return ConnectionUtils.ToConnection(resultSetIds.Items.Select(RtEntityDtoType.CreateRtEntityDto), arg,
                resultSetIds.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSetIds.TotalCount,
                resultSetIds.Grouping);
        }

        var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync(sessionAccessor.Session,
            ckTypeId, dataQueryOperation, offset,
            arg.First);

        Logger.Debug("GraphQL query handling returning data");
        return ConnectionUtils.ToConnection(resultSet.Items.Select(RtEntityDtoType.CreateRtEntityDto), arg,
            resultSet.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSet.TotalCount, resultSet.Grouping);
    }

    private async Task<object?> ResolveRtEntitiesQuery(IResolveConnectionContext<object?> arg)
    {
        Logger.Debug("GraphQL query handling for specific runtime entity type started");

        var sessionAccessor = arg.RequestServices?.GetRequiredService<IOctoSessionAccessor>();
        if (sessionAccessor?.Session == null)
        {
            throw AssetRepositoryException.SessionUnavailable();
        }

        var graphQlUserContext = (GraphQlUserContext)arg.UserContext;

        if (!arg.FieldDefinition.Metadata.TryGetValue(Statics.CkId, out var ckIdObj))
        {
            arg.Errors.Add(new ExecutionError("Invalid query. Missing construction kit id.")
                { Code = Statics.GraphQLErrorCommon });
            return null;
        }

        if (ckIdObj is not CkId<CkTypeId> ckTypeId)
        {
            arg.Errors.Add(new ExecutionError("Invalid query. Invalid construction kit id.")
                { Code = Statics.GraphQLErrorCommon });
            return null;
        }

        var offset = arg.GetOffset();
        var dataQueryOperation = arg.GetDataQueryOperation();

        var keysList = new List<OctoObjectId>();
        if (arg.TryGetArgument(Statics.RtIdArg, out OctoObjectId? rtId))
        {
            keysList.Add(rtId.Value);
        }

        if (arg.TryGetArgument(Statics.RtIdsArg, null, out IEnumerable<OctoObjectId>? rtIds))
        {
            keysList.AddRange(rtIds);
        }

        // if argument defined, but empty array, do not return any data. That mus be a mistake by client (otherwise
        // all entities are returned.
        if (!keysList.Any() && (arg.HasArgument(Statics.RtIdArg) || arg.HasArgument(Statics.RtIdsArg)))
        {
            return ConnectionUtils.ToConnection(new List<RtEntityDto>(), arg, null);
        }

        var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();
        if (keysList.Any())
        {
            var resultSetIds =
                await tenantRepository.GetRtEntitiesByIdAsync(
                    sessionAccessor.Session, ckTypeId, keysList, dataQueryOperation,
                    offset, arg.First);

            Logger.Debug("GraphQL query handling returning data by keys");
            return ConnectionUtils.ToConnection(resultSetIds.Items.Select(RtEntityDtoType.CreateRtEntityDto), arg,
                resultSetIds.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSetIds.TotalCount,
                resultSetIds.Grouping);
        }

        var resultSet =
            await tenantRepository.GetRtEntitiesByTypeAsync(sessionAccessor.Session,
                ckTypeId, dataQueryOperation, offset,
                arg.First);

        Logger.Debug("GraphQL query handling returning data");
        return ConnectionUtils.ToConnection(resultSet.Items.Select(RtEntityDtoType.CreateRtEntityDto), arg,
            resultSet.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSet.TotalCount, resultSet.Grouping);
    }
}