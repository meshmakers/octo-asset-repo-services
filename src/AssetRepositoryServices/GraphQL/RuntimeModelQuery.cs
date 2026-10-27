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
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
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

        AddByInterfaceField(graphTypesCache);

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

    /// <summary>
    ///     AB#6932: <c>runtime.byInterface</c>, a thin layer over the one engine method
    ///     <c>ITenantRepository.GetRtEntitiesByInterfaceAsync</c> (AB#7019): no fan-out, merge or security logic here.
    ///     Added only when at least one concrete type implements a CK interface, so the schema of a tenant without
    ///     interfaces does not change.
    /// </summary>
    private void AddByInterfaceField(IGraphTypesCache graphTypesCache)
    {
        var implementers = graphTypesCache.GetCkInterfaceImplementers();
        if (implementers.Count == 0)
        {
            return;
        }

        var unionType = new RtEntityByInterfaceUnionType(implementers);
        var connectionType = new RtEntityByInterfaceConnectionType(unionType);

        Field(Statics.ByInterfaceField, connectionType)
            .Description("Entities of all types that implement a CK interface, in one global order. Filter and sort " +
                         "only on members of the interface (or system fields). 'take' is required; skip + take is at " +
                         "most 1000. Text search, aggregations, navigation and geospatial filters are refused with " +
                         "the error code INTERFACE_QUERY_REFUSED (extensions: messageNumber 7019, reason, parameters).")
            .Argument<NonNullGraphType<StringGraphType>>(Statics.CkInterfaceIdArg,
                "The runtime id of the CK interface, for example \"System.Identity/Named-1\".")
            .Argument<NonNullGraphType<IntGraphType>>(Statics.TakeArg,
                "Number of entities to return (required). skip + take must not exceed 1000.")
            .Argument<IntGraphType>(Statics.SkipArg, "Number of entities to skip in the global order (default 0).",
                argument => argument.DefaultValue = 0)
            .Argument<GlobalQueryOptionsDtoType>(Statics.OptionsArg,
                "Global options to apply to the query, for example to include archived items.")
            .Argument<ListGraphType<SortDtoType>>(Statics.SortOrderArg, "Sort order for items")
            .Argument<ListGraphType<FieldFilterDtoType>>(Statics.FieldFilterArg,
                "Filters items based on field compare")
            .Argument<SearchFilterDtoType>(Statics.SearchFilterArg, "Not supported: refused with INTERFACE_QUERY_REFUSED.")
            .Argument<ResultAggregationInputDtoType>(Statics.AggregationsArg,
                "Not supported: refused with INTERFACE_QUERY_REFUSED.")
            .Argument<NearGeospatialFilterDtoType>(Statics.GeoNearFilterArg,
                "Not supported: refused with INTERFACE_QUERY_REFUSED.")
            .ResolveAsync(ResolveByInterfaceAsync);
    }

    private async Task<object?> ResolveByInterfaceAsync(IResolveFieldContext arg)
    {
        try
        {
            _logger.LogDebug("GraphQL query handling for runtime.byInterface started");

            var sessionAccessor = arg.GetSessionAccessor();
            var graphQlUserContext = (GraphQlUserContext)arg.UserContext;

            var ckInterfaceIdText = arg.GetArgument<string>(Statics.CkInterfaceIdArg);
            RtCkId<CkInterfaceId> ckInterfaceId;
            try
            {
                ckInterfaceId = new RtCkId<CkInterfaceId>(ckInterfaceIdText);
            }
            catch (Exception e) when (e is ArgumentException or FormatException)
            {
                throw new InterfaceQueryArgumentException(Statics.CkInterfaceIdArg,
                    $"'{ckInterfaceIdText}' is not a runtime CK interface id (expected 'Model/Interface-Version').");
            }

            var take = arg.GetArgument<int>(Statics.TakeArg);
            var skip = arg.GetArgument<int?>(Statics.SkipArg) ?? 0;
            if (take < 0)
            {
                throw new InterfaceQueryArgumentException(Statics.TakeArg, "take must not be negative.");
            }

            if (skip < 0)
            {
                throw new InterfaceQueryArgumentException(Statics.SkipArg, "skip must not be negative.");
            }

            var queryOptions = arg.GetQueryOptions();

            // The attribute guards (secret, hidden) are bound to one type graph: apply them to every implementing
            // type, so no type's secret or hidden attribute can be used as a filter or sort oracle through the interface.
            var ckCacheService = arg.GetCkCacheService();
            var ckInterfaceGraph = TryGetCkInterface(ckCacheService, graphQlUserContext.TenantId, ckInterfaceId);
            if (ckInterfaceGraph != null)
            {
                foreach (var implementingType in ckInterfaceGraph.ImplementingTypes)
                {
                    SecretQueryGuard.EnsureQueryable(ckCacheService, graphQlUserContext.TenantId,
                        implementingType.ToRtCkId(), queryOptions);
                }
            }

            var includeTotalCount = arg.IsFieldSelected("totalCount");
            var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();
            var result = await tenantRepository.GetRtEntitiesByInterfaceAsync(sessionAccessor.Session, ckInterfaceId,
                queryOptions, skip, take, includeTotalCount);

            _logger.LogDebug("GraphQL query handling returning data");
            return new RtEntityByInterfaceConnection(
                result.Items.Select(RtEntityDtoType.CreateRtEntityDto).ToList(),
                result.TotalCount is { } total ? (int)Math.Min(total, int.MaxValue) : null);
        }
        catch (Exception e)
        {
            return arg.HandleException(e);
        }
    }

    // An unknown interface is reported by the engine (reason UnknownInterface); the guard never turns it into another error.
    private static CkInterfaceGraph? TryGetCkInterface(ICkCacheService ckCacheService, string tenantId,
        RtCkId<CkInterfaceId> ckInterfaceId)
    {
        try
        {
            return ckCacheService.GetRtCkInterface(tenantId, ckInterfaceId);
        }
        catch (Exception e) when (e is CkCacheException or KeyNotFoundException)
        {
            return null;
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

        // Validate grouping columns
        var groupingColumns = rtGroupingAggregationRtQuery.GroupingColumns?.ToList() ?? [];

        var typeQueryColumnPaths = QueryColumnPathResolver.GetColumnsForPaths(ckCacheService,
            graphQlUserContext.TenantId, rtGroupingAggregationRtQuery.QueryCkTypeId,
            groupingColumns.Concat(rtGroupingAggregationRtQuery.Columns.Select(c => c.AttributePath)).ToList());
        var invalidGroupingColumns = groupingColumns
            .Where(cp => typeQueryColumnPaths.All(ckTypeQueryColumn => ckTypeQueryColumn.Path != cp)).ToList();
        if (invalidGroupingColumns.Any())
        {
            SecretQueryGuard.EnsureNoSecretColumns(ckCacheService, graphQlUserContext.TenantId, rtGroupingAggregationRtQuery.QueryCkTypeId,
                invalidGroupingColumns);
            throw AssetRepositoryException.InvalidColumnPaths(invalidGroupingColumns);
        }

        // Validate aggregation columns
        var invalidColumnPaths = rtGroupingAggregationRtQuery.Columns
            .Where(cp => typeQueryColumnPaths.All(ckTypeQueryColumn => ckTypeQueryColumn.Path != cp.AttributePath)).ToList();
        if (invalidColumnPaths.Any())
        {
            SecretQueryGuard.EnsureNoSecretColumns(ckCacheService, graphQlUserContext.TenantId, rtGroupingAggregationRtQuery.QueryCkTypeId,
                invalidColumnPaths.Select(p => p.AttributePath).ToList());
            throw AssetRepositoryException.InvalidColumnPaths(invalidColumnPaths.Select(p => p.AttributePath).ToList());
        }

        var selectedTypeQueryColumns = typeQueryColumnPaths
            .Join(rtGroupingAggregationRtQuery.Columns,
                ckTypeQueryColumn => ckTypeQueryColumn.Path,
                column => column.AttributePath,
                (ckTypeQueryColumn, column) => Tuple.Create(ckTypeQueryColumn, MapAggregationType(column.AggregationType)))
            .ToList();

        // Resolve groupBy column paths to CkTypeQueryColumn objects
        var groupByTypeQueryColumns = typeQueryColumnPaths
            .Where(c => groupingColumns.Contains(c.Path))
            .ToList();

        _logger.LogDebug("GraphQL query handling returning data");
        return ConnectionUtils.ToOctoConnection(
            [RtQueryDtoType.CreateRtQueryDto(rtGroupingAggregationRtQuery, selectedTypeQueryColumns, groupByTypeQueryColumns)], arg,
            0, 1);
    }

    private OctoConnection<RtQueryDto> ResolveRtAggregationRtQueryAsync(IResolveConnectionContext<object?> arg,
        RtAggregationRtQuery rtAggregationRtQuery)
    {
        var ckCacheService = arg.GetCkCacheService();
        var graphQlUserContext = (GraphQlUserContext)arg.UserContext;

        var typeQueryColumnPaths = QueryColumnPathResolver.GetColumnsForPaths(ckCacheService,
            graphQlUserContext.TenantId, rtAggregationRtQuery.QueryCkTypeId,
            rtAggregationRtQuery.Columns.Select(c => c.AttributePath).ToList());
        var invalidColumnPaths = rtAggregationRtQuery.Columns.Where(cp => typeQueryColumnPaths.All(ckTypeQueryColumn => ckTypeQueryColumn.Path != cp.AttributePath)).ToList();
        if (invalidColumnPaths.Any())
        {
            SecretQueryGuard.EnsureNoSecretColumns(ckCacheService, graphQlUserContext.TenantId, rtAggregationRtQuery.QueryCkTypeId,
                invalidColumnPaths.Select(p=> p.AttributePath).ToList());
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

        var storedColumnPaths = rtSimpleRtQuery.Columns.ToList();
        var typeQueryColumnPaths = QueryColumnPathResolver.GetColumnsForPaths(ckCacheService,
            graphQlUserContext.TenantId, rtSimpleRtQuery.QueryCkTypeId, storedColumnPaths);
        var resolvedColumns = storedColumnPaths
            .Select(cp => (Path: cp, Column: QueryColumnPathResolver.TryResolveColumn(typeQueryColumnPaths, cp)))
            .ToList();
        var invalidColumnPaths = resolvedColumns.Where(rc => rc.Column == null).Select(rc => rc.Path).ToList();
        if (invalidColumnPaths.Any())
        {
            SecretQueryGuard.EnsureNoSecretColumns(ckCacheService, graphQlUserContext.TenantId, rtSimpleRtQuery.QueryCkTypeId,
                invalidColumnPaths);
            throw AssetRepositoryException.InvalidColumnPaths(invalidColumnPaths);
        }

        var selectedTypeQueryColumns = resolvedColumns
            .Select(rc => Tuple.Create(rc.Column!, AggregationTypesDto.None))
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
            SecretQueryGuard.EnsureQueryable(arg.GetCkCacheService(), graphQlUserContext.TenantId, ckTypeId,
                queryOptions);

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
            SecretQueryGuard.EnsureQueryable(arg.GetCkCacheService(), graphQlUserContext.TenantId, ckTypeId,
                rtEntityQueryOptions);

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