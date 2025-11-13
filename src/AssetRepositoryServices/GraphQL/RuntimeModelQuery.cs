using AssetRepositoryServices.Resources;
using GraphQL;
using GraphQL.Builders;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v1;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

[DoNotRegister]
internal sealed class RuntimeModelQuery : ObjectGraphType
{
    private readonly ILogger<RuntimeModelQuery> _logger;

    public RuntimeModelQuery(ILogger<RuntimeModelQuery> logger, IGraphTypesCache graphTypesCache)
    {
        _logger = logger;
        Name = "RuntimeModelQuery";

        Connection<RtEntityGenericDtoType>("RuntimeEntities")
            .Argument<StringGraphType>(Statics.CkIdArg, "The construction kit type with the given id.")
            .Argument<GlobalQueryOptionsDtoType>(Statics.OptionsArg, "Global options to apply to the query, for example to include archived items.")
            .Argument<OctoObjectIdType>(Statics.RtIdArg, "Returns the entity with the given rtId.")
            .Argument<ListGraphType<OctoObjectIdType>>(Statics.RtIdsArg,
                "Returns entities with the given rtIds.")
            .Argument<SearchFilterDtoType>(Statics.SearchFilterArg, "Filters items based on text search")
            .Argument<ResultAggregationInputDtoType>(Statics.AggregationsArg,
                AssetTexts.Graphql_Type_Filter_Aggregations_Description)
            .Argument<ListGraphType<SortDtoType>>(Statics.SortOrderArg, "Sort order for items")
            .Argument<ListGraphType<FieldFilterDtoType>>(Statics.FieldFilterArg,
                "Filters items based on field compare")
            .ResolveAsync(ResolveGenericRtEntitiesQuery);


        Connection<NonNullGraphType<RtQueryDtoType>>("RuntimeQuery")
            .Argument<NonNullGraphType<OctoObjectIdType>>(Statics.RtIdArg, "The query runtime id.")
            .ResolveAsync(ResolveRtQueryAsync);

        Connection<NonNullGraphType<RtTransientQueryDtoType>>("TransientRuntimeQuery")
            .Argument<NonNullGraphType<StringGraphType>>(Statics.CkIdArg,
                "The construction kit type with the given id.")
            .Argument<NonNullGraphType<ListGraphType<NonNullGraphType<StringGraphType>>>>(Statics.ColumnPathsArg,
                "The column paths to include in the result.")
            .Argument<SearchFilterDtoType>(Statics.SearchFilterArg, "Filters items based on text search")
            .Argument<ListGraphType<SortDtoType>>(Statics.SortOrderArg, "Sort order for items")
            .Argument<ListGraphType<FieldFilterDtoType>>(Statics.FieldFilterArg,
                "Filters items based on field compare")
            .Resolve(ResolveTransientRtQuery);

        foreach (var rtEntityDtoType in graphTypesCache.GetTypes())
        {
            this.Connection<object?, IGraphType, RtEntityDto>(graphTypesCache, rtEntityDtoType, rtEntityDtoType.Name)
                .AddMetadata(Statics.CkId, rtEntityDtoType.CkTypeId.ToRtCkId())
                .Argument<GlobalQueryOptionsDtoType>(Statics.OptionsArg, "Global options to apply to the query, for example to include archived items.")
                .Argument<OctoObjectIdType>(Statics.RtIdArg, "Returns the entity with the given rtId.")
                .Argument<ListGraphType<OctoObjectIdType>>(Statics.RtIdsArg,
                    "Returns entities with the given rtIds.")
                .Argument<SearchFilterDtoType>(Statics.SearchFilterArg, "Filters items based on text search")
                .Argument<ResultAggregationInputDtoType>(Statics.AggregationsArg,
                    AssetTexts.Graphql_Type_Filter_Aggregations_Description)
                .Argument<ListGraphType<SortDtoType>>(Statics.SortOrderArg, "Sort order for items")
                .Argument<NearGeospatialFilterDtoType>(Statics.GeoNearFilterArg,
                    "Geospatial filter for items, that searches for items near a point")
                .Argument<ListGraphType<FieldFilterDtoType>>(Statics.FieldFilterArg,
                    "Filters items based on field compare")
                .ResolveAsync(ResolveRtEntitiesQuery);
        }
    }

    private object ResolveTransientRtQuery(IResolveConnectionContext<object?> arg)
    {
        _logger.LogDebug("GraphQL query handling for transient runtime query started");

        var ckCacheService = arg.GetCkCacheService();

        var graphQlUserContext = (GraphQlUserContext)arg.UserContext;
        var ckTypeId = arg.GetArgument<RtCkId<CkTypeId>>(Statics.CkId);

        var columnPaths = arg.GetArgument<IEnumerable<string>>(Statics.ColumnPathsArg);
        var columnPathList = columnPaths.ToList();

        var typeQueryColumnPaths = ckCacheService.GetCkTypeQueryColumnPathsByRtCkId(graphQlUserContext.TenantId, ckTypeId);
        var invalidColumnPaths = columnPathList
            .Where(cp => typeQueryColumnPaths.All(ckTypeQueryColumn => ckTypeQueryColumn.Path != cp)).ToList();
        if (invalidColumnPaths.Any())
        {
            throw AssetRepositoryException.InvalidColumnPaths(invalidColumnPaths);
        }

        var selectedTypeQueryColumns = typeQueryColumnPaths
            .Where(ckTypeQueryColumn => columnPathList.Contains(ckTypeQueryColumn.Path)).ToList();

        var queryOptions = arg.GetQueryOptions();

        _logger.LogDebug("GraphQL query handling returning data");
        return ConnectionUtils.ToConnection(
            [RtTransientQueryDtoType.CreateTransientRtQueryDto(ckTypeId, queryOptions, selectedTypeQueryColumns)],
            arg,
            0, 1);
    }

    private async Task<object?> ResolveRtQueryAsync(IResolveConnectionContext<object?> arg)
    {
        try
        {
            _logger.LogDebug("GraphQL query handling for runtime query started");

            var ckCacheService = arg.GetCkCacheService();
            var sessionAccessor = arg.GetSessionAccessor();

            var graphQlUserContext = (GraphQlUserContext)arg.UserContext;
            var queryRtId = arg.GetArgument<OctoObjectId>(Statics.RtIdArg);

            var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();
            var rtQuery =
                await tenantRepository.GetRtEntityByRtIdAsync<RtQuery>(
                    sessionAccessor.Session, queryRtId);

            if (rtQuery == null)
            {
                throw AssetRepositoryException.RtQueryNotFound(queryRtId);
            }

            var typeQueryColumnPaths =
                ckCacheService.GetCkTypeQueryColumnPaths(graphQlUserContext.TenantId, rtQuery.QueryCkTypeId);
            var invalidColumnPaths = rtQuery.Columns
                .Where(cp => typeQueryColumnPaths.All(ckTypeQueryColumn => ckTypeQueryColumn.Path != cp)).ToList();
            if (invalidColumnPaths.Any())
            {
                throw AssetRepositoryException.InvalidColumnPaths(invalidColumnPaths);
            }

            var selectedTypeQueryColumns = typeQueryColumnPaths
                .Where(ckTypeQueryColumn => rtQuery.Columns.Contains(ckTypeQueryColumn.Path)).ToList();

            _logger.LogDebug("GraphQL query handling returning data");
            return ConnectionUtils.ToConnection(
                [RtQueryDtoType.CreateRtQueryDto(rtQuery, selectedTypeQueryColumns)], arg,
                0, 1);
        }
        catch (Exception e)
        {
            return arg.HandleException(e);
        }
    }

    private async Task<object?> ResolveGenericRtEntitiesQuery(IResolveConnectionContext<object?> arg)
    {
        try
        {
            _logger.LogDebug("GraphQL query handling for generic runtime entity started");

            var sessionAccessor = arg.GetSessionAccessor();

            var graphQlUserContext = (GraphQlUserContext)arg.UserContext;
            var ckTypeId = arg.GetArgument<RtCkId<CkTypeId>>(Statics.CkId);

            var offset = arg.GetOffset();
            var queryOptions = arg.GetQueryOptions();

            var keysList = new List<OctoObjectId>();
            if (arg.TryGetArgument(Statics.RtIdArg, out OctoObjectId? rtId))
            {
                keysList.Add(rtId.Value);
            }

            if (arg.TryGetArgument(Statics.RtIdsArg, null, out IEnumerable<OctoObjectId>? rtIds))
            {
                keysList.AddRange(rtIds);
            }

            // If argument defined, but empty array, do not return any data.
            // That must be a mistake by client (otherwise all entities are returned).
            if (!keysList.Any() && (arg.HasArgument(Statics.RtIdArg) || arg.HasArgument(Statics.RtIdsArg)))
            {
                return ConnectionUtils.ToConnection(new List<RtEntityDto>(), arg);
            }

            var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();
            if (keysList.Any())
            {
                var resultSetIds = await tenantRepository.GetRtEntitiesByIdAsync(
                    sessionAccessor.Session, ckTypeId, keysList, queryOptions,
                    offset, arg.First);

                _logger.LogDebug("GraphQL query handling returning data by keys");
                return ConnectionUtils.ToConnection(resultSetIds.Items.Select(RtEntityDtoType.CreateRtEntityDto), arg,
                    resultSetIds.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSetIds.TotalCount,
                    resultSetIds.AggregationResult, resultSetIds.FieldAggregationResult);
            }

            var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync(sessionAccessor.Session,
                ckTypeId, queryOptions, offset,
                arg.First);

            _logger.LogDebug("GraphQL query handling returning data");
            return ConnectionUtils.ToConnection(resultSet.Items.Select(RtEntityDtoType.CreateRtEntityDto), arg,
                resultSet.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSet.TotalCount,
                resultSet.AggregationResult, resultSet.FieldAggregationResult);
        }
        catch (Exception e)
        {
            return arg.HandleException(e);
        }
    }

    private async Task<object?> ResolveRtEntitiesQuery(IResolveConnectionContext<object?> arg)
    {
        try
        {
            _logger.LogDebug("GraphQL query handling for specific runtime entity type started");

            var sessionAccessor = arg.GetSessionAccessor();
            var graphQlUserContext = (GraphQlUserContext)arg.UserContext;

            var ckTypeId = arg.GetMetadataValue<RtCkId<CkTypeId>>(Statics.CkId);

            var offset = arg.GetOffset();
            var rtEntityQueryOptions = arg.GetQueryOptions();

            var keysList = new List<OctoObjectId>();
            if (arg.TryGetArgument(Statics.RtIdArg, out OctoObjectId? rtId))
            {
                keysList.Add(rtId.Value);
            }

            if (arg.TryGetArgument(Statics.RtIdsArg, null, out IEnumerable<OctoObjectId>? rtIds))
            {
                keysList.AddRange(rtIds);
            }

            // If argument defined, but empty array, do not return any data.
            // That must be a mistake by client (otherwise all entities are returned)
            if (!keysList.Any() && (arg.HasArgument(Statics.RtIdArg) || arg.HasArgument(Statics.RtIdsArg)))
            {
                return ConnectionUtils.ToConnection(new List<RtEntityDto>(), arg);
            }

            var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();
            if (keysList.Any())
            {
                var resultSetIds =
                    await tenantRepository.GetRtEntitiesByIdAsync(
                        sessionAccessor.Session, ckTypeId, keysList, rtEntityQueryOptions,
                        offset, arg.First);

                _logger.LogDebug("GraphQL query handling returning data by keys");
                return ConnectionUtils.ToConnection(resultSetIds.Items.Select(RtEntityDtoType.CreateRtEntityDto), arg,
                    resultSetIds.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSetIds.TotalCount,
                    resultSetIds.AggregationResult, resultSetIds.FieldAggregationResult);
            }

            var resultSet =
                await tenantRepository.GetRtEntitiesByTypeAsync(sessionAccessor.Session,
                    ckTypeId, rtEntityQueryOptions, offset,
                    arg.First);

            _logger.LogDebug("GraphQL query handling returning data");
            return ConnectionUtils.ToConnection(resultSet.Items.Select(RtEntityDtoType.CreateRtEntityDto), arg,
                resultSet.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSet.TotalCount,
                resultSet.AggregationResult, resultSet.FieldAggregationResult);
        }
        catch (Exception e)
        {
            return arg.HandleException(e);
        }
    }
}