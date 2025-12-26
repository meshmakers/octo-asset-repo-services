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
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;

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
            .Argument<GlobalQueryOptionsDtoType>(Statics.OptionsArg,
                "Global options to apply to the query, for example to include archived items.")
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

        Field<NonNullGraphType<RtTransientQuery>>("TransientQuery")
            .Description("Transient runtime queries")
            .Resolve(_ => new { });

        foreach (var rtEntityDtoType in graphTypesCache.GetTypes())
        {
            this.Connection<object?, IGraphType, RtEntityDto>(graphTypesCache, rtEntityDtoType, rtEntityDtoType.Name)
                .AddMetadata(Statics.CkId, rtEntityDtoType.CkTypeId.ToRtCkId())
                .Argument<GlobalQueryOptionsDtoType>(Statics.OptionsArg,
                    "Global options to apply to the query, for example to include archived items.")
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

    private async Task<object?> ResolveRtQueryAsync(IResolveConnectionContext<object?> arg)
    {
        try
        {
            _logger.LogDebug("GraphQL query handling for runtime query started");

            var sessionAccessor = arg.GetSessionAccessor();

            var queryRtId = arg.GetArgument<OctoObjectId>(Statics.RtIdArg);

            var graphQlUserContext = (GraphQlUserContext)arg.UserContext;
            var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();
            var rtQuery =
                await tenantRepository.GetRtEntityByRtIdAsync<RtPersistentQuery>(
                    sessionAccessor.Session, queryRtId);

            if (rtQuery == null)
            {
                throw AssetRepositoryException.RtQueryNotFound(queryRtId);
            }

            // Check the type of rtQuery
            if (rtQuery.GetType() == typeof(RtSimpleRtQuery))
            {
                return ResolveRtSimpleRtQueryAsync(arg, (RtSimpleRtQuery)rtQuery);
            }

            if (rtQuery.GetType() == typeof(RtAggregationRtQuery))
            {
                return ResolveRtAggregationRtQueryAsync(arg, (RtAggregationRtQuery)rtQuery);
            }

            if (rtQuery.GetType() == typeof(RtGroupingAggregationRtQuery))
            {
                return ResolveRtGroupingAggregationRtQueryAsync(arg, (RtGroupingAggregationRtQuery)rtQuery);
            }

            throw AssetRepositoryException.RtQueryTypeUnknown(rtQuery.GetType().Name);
        }
        catch (Exception e)
        {
            return arg.HandleException(e);
        }
    }

    private OctoConnection<RtQueryDto> ResolveRtGroupingAggregationRtQueryAsync(IResolveConnectionContext<object?> arg,
        RtGroupingAggregationRtQuery rtGroupingAggregationRtQuery)
    {
        var ckCacheService = arg.GetCkCacheService();
        var graphQlUserContext = (GraphQlUserContext)arg.UserContext;

        var typeQueryColumnPaths =
            ckCacheService.GetCkTypeQueryColumnPathsByRtCkId(graphQlUserContext.TenantId,
                rtGroupingAggregationRtQuery.QueryCkTypeId);

        // Validate grouping columns
        var groupingColumns = rtGroupingAggregationRtQuery.GroupingColumns?.ToList() ?? [];
        var invalidGroupingColumns = groupingColumns
            .Where(cp => typeQueryColumnPaths.All(ckTypeQueryColumn => ckTypeQueryColumn.Path != cp)).ToList();
        if (invalidGroupingColumns.Any())
        {
            throw AssetRepositoryException.InvalidColumnPaths(invalidGroupingColumns);
        }

        // Validate aggregation columns
        var invalidColumnPaths = rtGroupingAggregationRtQuery.Columns
            .Where(cp => typeQueryColumnPaths.All(ckTypeQueryColumn => ckTypeQueryColumn.Path != cp.AttributePath)).ToList();
        if (invalidColumnPaths.Any())
        {
            throw AssetRepositoryException.InvalidColumnPaths(invalidColumnPaths.Select(p => p.AttributePath).ToList());
        }

        var selectedTypeQueryColumns = typeQueryColumnPaths
            .Join(rtGroupingAggregationRtQuery.Columns,
                ckTypeQueryColumn => ckTypeQueryColumn.Path,
                column => column.AttributePath,
                (ckTypeQueryColumn, column) => Tuple.Create(ckTypeQueryColumn, MapAggregationType(column.AggregationType)))
            .ToList();

        _logger.LogDebug("GraphQL query handling returning data");
        return ConnectionUtils.ToOctoConnection(
            [RtQueryDtoType.CreateRtQueryDto(rtGroupingAggregationRtQuery, selectedTypeQueryColumns, groupingColumns)], arg,
            0, 1);
    }

    private OctoConnection<RtQueryDto> ResolveRtAggregationRtQueryAsync(IResolveConnectionContext<object?> arg,
        RtAggregationRtQuery rtAggregationRtQuery)
    {
        var ckCacheService = arg.GetCkCacheService();
        var graphQlUserContext = (GraphQlUserContext)arg.UserContext;

        var typeQueryColumnPaths =
            ckCacheService.GetCkTypeQueryColumnPathsByRtCkId(graphQlUserContext.TenantId,
                rtAggregationRtQuery.QueryCkTypeId);
        var invalidColumnPaths = rtAggregationRtQuery.Columns.Where(cp => typeQueryColumnPaths.All(ckTypeQueryColumn => ckTypeQueryColumn.Path != cp.AttributePath)).ToList();
        if (invalidColumnPaths.Any())
        {
            throw AssetRepositoryException.InvalidColumnPaths(invalidColumnPaths.Select(p=> p.AttributePath).ToList());
        }

        var selectedTypeQueryColumns = typeQueryColumnPaths
            .Join(rtAggregationRtQuery.Columns,
                ckTypeQueryColumn => ckTypeQueryColumn.Path,
                column => column.AttributePath,
                (ckTypeQueryColumn, column) => Tuple.Create(ckTypeQueryColumn, MapAggregationType(column.AggregationType)))
            .ToList();

        _logger.LogDebug("GraphQL query handling returning data");
        return ConnectionUtils.ToOctoConnection(
            [RtQueryDtoType.CreateRtQueryDto(rtAggregationRtQuery, selectedTypeQueryColumns)], arg,
            0, 1);
    }

    private OctoConnection<RtQueryDto> ResolveRtSimpleRtQueryAsync(IResolveConnectionContext<object?> arg,
        RtSimpleRtQuery rtSimpleRtQuery)
    {
        var ckCacheService = arg.GetCkCacheService();
        var graphQlUserContext = (GraphQlUserContext)arg.UserContext;

        var typeQueryColumnPaths =
            ckCacheService.GetCkTypeQueryColumnPathsByRtCkId(graphQlUserContext.TenantId,
                rtSimpleRtQuery.QueryCkTypeId);
        var invalidColumnPaths = rtSimpleRtQuery.Columns
            .Where(cp => typeQueryColumnPaths.All(ckTypeQueryColumn => ckTypeQueryColumn.Path != cp)).ToList();
        if (invalidColumnPaths.Any())
        {
            throw AssetRepositoryException.InvalidColumnPaths(invalidColumnPaths);
        }

        var selectedTypeQueryColumns = typeQueryColumnPaths
            .Where(ckTypeQueryColumn => rtSimpleRtQuery.Columns.Contains(ckTypeQueryColumn.Path))
            .Select(ckTypeQueryColumn => Tuple.Create(ckTypeQueryColumn, AggregationTypesDto.None))
            .ToList();

        _logger.LogDebug("GraphQL query handling returning data");
        return ConnectionUtils.ToOctoConnection(
            [RtQueryDtoType.CreateRtQueryDto(rtSimpleRtQuery, selectedTypeQueryColumns)], arg,
            0, 1);
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
                return ConnectionUtils.ToOctoConnection(new List<RtEntityDto>(), arg);
            }

            var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();
            if (keysList.Any())
            {
                var resultSetIds = await tenantRepository.GetRtEntitiesByIdAsync(
                    sessionAccessor.Session, ckTypeId, keysList, queryOptions,
                    offset, arg.First);

                _logger.LogDebug("GraphQL query handling returning data by keys");
                return ConnectionUtils.ToOctoConnection(resultSetIds.Items.Select(RtEntityDtoType.CreateRtEntityDto), arg,
                    resultSetIds.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSetIds.TotalCount,
                    resultSetIds.AggregationResult, resultSetIds.FieldAggregationResult);
            }

            var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync(sessionAccessor.Session,
                ckTypeId, queryOptions, offset,
                arg.First);

            _logger.LogDebug("GraphQL query handling returning data");
            return ConnectionUtils.ToOctoConnection(resultSet.Items.Select(RtEntityDtoType.CreateRtEntityDto), arg,
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
                return ConnectionUtils.ToOctoConnection(new List<RtEntityDto>(), arg);
            }

            var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();
            if (keysList.Any())
            {
                var resultSetIds =
                    await tenantRepository.GetRtEntitiesByIdAsync(
                        sessionAccessor.Session, ckTypeId, keysList, rtEntityQueryOptions,
                        offset, arg.First);

                _logger.LogDebug("GraphQL query handling returning data by keys");
                return ConnectionUtils.ToOctoConnection(resultSetIds.Items.Select(RtEntityDtoType.CreateRtEntityDto), arg,
                    resultSetIds.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSetIds.TotalCount,
                    resultSetIds.AggregationResult, resultSetIds.FieldAggregationResult);
            }

            var resultSet =
                await tenantRepository.GetRtEntitiesByTypeAsync(sessionAccessor.Session,
                    ckTypeId, rtEntityQueryOptions, offset,
                    arg.First);

            _logger.LogDebug("GraphQL query handling returning data");
            return ConnectionUtils.ToOctoConnection(resultSet.Items.Select(RtEntityDtoType.CreateRtEntityDto), arg,
                resultSet.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSet.TotalCount,
                resultSet.AggregationResult, resultSet.FieldAggregationResult);
        }
        catch (Exception e)
        {
            return arg.HandleException(e);
        }
    }

    private static AggregationTypesDto MapAggregationType(Enum aggregationType)
    {
        // Map by name since the generated System enum has different numeric values than AggregationTypesDto
        var typeName = aggregationType.ToString();
        return typeName switch
        {
            "Count" => AggregationTypesDto.Count,
            "Sum" => AggregationTypesDto.Sum,
            "Average" => AggregationTypesDto.Average,
            "Minimum" => AggregationTypesDto.Minimum,
            "Maximum" => AggregationTypesDto.Maximum,
            _ => throw new ArgumentOutOfRangeException(nameof(aggregationType), aggregationType, $"Unknown aggregation type: {typeName}")
        };
    }
}