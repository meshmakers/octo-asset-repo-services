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
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Services.StreamData;
using Meshmakers.Octo.Services.StreamData.Dtos;
using Meshmakers.Octo.Services.StreamData.QueryBuilder;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

[DoNotRegister]
internal sealed class StreamDataQuery : ObjectGraphType
{
    private readonly ILogger<StreamDataQuery> _logger;

    public StreamDataQuery(ILogger<StreamDataQuery> logger, IGraphTypesCache graphTypesCache)
    {
        _logger = logger;
        Name = "StreamDataModelQuery";

        Connection<NonNullGraphType<StreamDataQueryRowDtoType>>("StreamDataQuery")
            .Argument<NonNullGraphType<OctoObjectIdType>>(Statics.RtIdArg, "The persisted stream data query runtime id.")
            .Argument<StreamDataArgumentsGraphType>(Statics.StreamDataArgument, "Override time filter and limit at execution time.")
            .Argument<ListGraphType<SortDtoType>>(Statics.SortOrderArg, "Sort order for items")
            .ResolveAsync(ResolveStreamDataRtQueryAsync);

        foreach (var rtEntityDtoType in graphTypesCache.GetStreamTypes())
        {
            this.Connection<object?, IGraphType, StreamDataEntityDto>(graphTypesCache, rtEntityDtoType,
                    rtEntityDtoType.ConnectionName)
                .AddMetadata(Statics.CkId, rtEntityDtoType.CkTypeId.ToRtCkId())
                .Argument<OctoObjectIdType>(Statics.RtIdArg, "Returns the entity with the given rtId.")
                .Argument<ListGraphType<OctoObjectIdType>>(Statics.RtIdsArg, "Returns entities with the given rtIds.")
                .Argument<StreamDataArgumentsGraphType>(Statics.StreamDataArgument, "Filter for stream data data.")
                .Argument<ListGraphType<SortDtoType>>(Statics.SortOrderArg, "Sort order for items")
                .ResolveAsync(ResolveRtEntitiesQuery);
        }
    }

    private async Task<object?> ResolveRtEntitiesQuery(IResolveConnectionContext<object?> arg)
    {
        try
        {
            _logger.LogDebug("GraphQL query handling for specific stream data entity type started");

            var fieldContext = FieldContext.FromContext(arg);

            var ckCacheService = arg.GetCkCacheService();
            var ckTypeId = arg.GetMetadataValue<RtCkId<CkTypeId>>(Statics.CkId);
            var graphQlUserContext = (GraphQlUserContext)arg.UserContext;
            var tenantId = graphQlUserContext.TenantId;
            var requestedType = ckCacheService.GetRtCkType(tenantId, ckTypeId);

            var q = new CrateQueryBuilder(tenantId);
            q.IncludeDefaultVariables();

            q.WithCkTypeIdFilter(requestedType.CkTypeId.ToRtCkId());

            var entityTimeFilter = fieldContext.GetArgument<StreamDataArguments>(Statics.StreamDataArgument);

            // Downsampling: keep existing behavior unchanged (LIMIT = bucket count, no pagination)
            if (entityTimeFilter is { QueryMode: QueryModeDto.Downsampling })
            {
                if (entityTimeFilter.From is null || entityTimeFilter.To is null || entityTimeFilter.Limit is null)
                {
                    throw AssetRepositoryException.InvalidStreamDataQueryParams();
                }

                q.WithDownsampling(entityTimeFilter.Limit.Value, entityTimeFilter.From.Value,
                    entityTimeFilter.To.Value);

                HandleRequestedAttributes(fieldContext, requestedType, q);

                if (!HandleRequestedRtIds(arg, q))
                {
                    return ConnectionUtils.ToOctoConnection(new List<RtEntityDto>(), arg);
                }

                var comp = new CrateQueryCompiler();
                var sql = comp.CompileQuery(q);
                _logger.LogDebug("Executing SQL query: {Sql}", sql);
                var streamDataDatabaseClient = arg.GetStreamDataDatabaseClient();
                var data = await streamDataDatabaseClient.GetDataAsync(tenantId, sql);
                _logger.LogDebug("SQL query executed. Got {Count} rows", data.Count);
                var result = data.Select(StreamDataEntityDtoType.CreateStreamDataEntityDto).ToList();
                return ConnectionUtils.ToOctoConnection(result, arg, 0, result.Count);
            }

            // Normal mode: store rowCap, apply time filter
            int? rowCap = null;
            if (entityTimeFilter is { From: not null, To: not null })
            {
                q.WithTimeFilter(entityTimeFilter.From.Value, entityTimeFilter.To.Value);
            }
            else if (entityTimeFilter is { Limit: not null })
            {
                rowCap = entityTimeFilter.Limit.Value;
            }

            HandleRequestedAttributes(fieldContext, requestedType, q);

            if (!HandleRequestedRtIds(arg, q))
            {
                return ConnectionUtils.ToOctoConnection(new List<RtEntityDto>(), arg);
            }

            // Database-level pagination
            var (pagedData, totalCount, effectiveOffset) = await ExecutePaginatedStreamDataQueryAsync(
                q, arg.GetStreamDataDatabaseClient(), tenantId, arg.GetOffset(), arg.First, rowCap);

            _logger.LogDebug("SQL query executed. Got {Count} rows, totalCount={TotalCount}", pagedData.Count, totalCount);

            var pagedResult = pagedData.Select(StreamDataEntityDtoType.CreateStreamDataEntityDto).ToList();
            return ConnectionUtils.ToOctoConnection(pagedResult, arg,
                pagedResult.Count != 0 ? effectiveOffset : 0, totalCount);
        }
        catch (Exception e)
        {
            return arg.HandleException(e);
        }
    }

    private bool HandleRequestedRtIds(IResolveConnectionContext<object?> arg, CrateQueryBuilder q)
    {
        var rtIdList = new List<OctoObjectId>();
        if (arg.TryGetArgument(Statics.RtIdArg, out OctoObjectId? rtId))
        {
            rtIdList.Add(rtId.Value);
        }

        if (arg.TryGetArgument(Statics.RtIdsArg, null, out IEnumerable<OctoObjectId>? rtIds))
        {
            rtIdList.AddRange(rtIds);
        }

        // if argument defined, but empty array, do not return any data. That mus be a mistake by client (otherwise
        // all entities are returned.
        if (!rtIdList.Any() && (arg.HasArgument(Statics.RtIdArg) || arg.HasArgument(Statics.RtIdsArg)))
        {
            return false;
        }

        if (rtIdList.Any())
        {
            var rtIdStrings = rtIdList.Select(x => x.ToString());
            q.AddWhereIn("RtId", rtIdStrings.ToArray());
            // arg.Errors.Add(new ExecutionError("Filtering by RtIds is not yet supported")
            // { Code = Statics.GraphQlStreamDataQueryError });
        }

        return true;
    }

    private static void HandleRequestedAttributes(FieldContext fieldContext, CkTypeGraph requestedType,
        CrateQueryBuilder q)
    {
        var itemField = fieldContext.Fields.FirstOrDefault(x => x.Name == Statics.ItemsQueryArg);
        if (itemField == null)
        {
            return;
        }

        var orderQueue = new PriorityQueue<Tuple<string, SortOrderDto>, int>();

        foreach (var field in itemField.Fields)
        {
            var dataStreamAttributes = requestedType.AllAttributes.Where(x => x.Value.IsDataStream);
            var argument = field.GetArgument<AttributeTsArgumentDto>(Statics.StreamDataAttributeArgument);

            bool ContainsField(KeyValuePair<CkId<CkAttributeId>, CkTypeAttributeGraph> x)
            {
                return string.Equals(x.Value.AttributeName, field.Name, StringComparison.InvariantCultureIgnoreCase);
            }

            var (_, requestedAttribute) = dataStreamAttributes
                .FirstOrDefault(ContainsField);

            if (requestedAttribute != null)
            {
                AddVariable(requestedAttribute.AttributeName, argument, true, q);
                if (argument?.SortPriority != null)
                {
                    orderQueue.Enqueue(
                        new Tuple<string, SortOrderDto>(requestedAttribute.AttributeName,
                            argument.SortOrder.GetValueOrDefault(SortOrderDto.Ascending)),
                        argument.SortPriority.GetValueOrDefault(0));
                }
            }


            var standardField = Constants.DefaultStreamDataFields.FirstOrDefault(x =>
                string.Equals(x, field.Name, StringComparison.InvariantCultureIgnoreCase));

            if (standardField == null)
            {
                continue;
            }

            if (argument?.SortPriority != null)
            {
                orderQueue.Enqueue(
                    new Tuple<string, SortOrderDto>(standardField,
                        argument.SortOrder.GetValueOrDefault(SortOrderDto.Ascending)),
                    argument.SortPriority.GetValueOrDefault(0));
            }

            AddVariable(standardField, argument, false, q);
        }


        while (orderQueue.Count > 0)
        {
            var order = orderQueue.Dequeue();
            q.OrderBy(order.Item1, order.Item2);
        }
    }

    private static void AddVariable(string name, AttributeTsArgumentDto? attribute, bool isDataVariable,
        CrateQueryBuilder q)
    {
        if (attribute is { AggregationType: not null })
        {
            q.AddAggregationVariable(name, attribute.AggregationType.Value, null, isDataVariable);
        }
        else
        {
            if (name == "Timestamp")
            {
                q.AddVariable("Timestamp", "T", null, isDataVariable);
            }
            else
            {
                q.AddVariable(name, name, null, isDataVariable);
            }
        }
    }

    private async Task<object?> ResolveStreamDataRtQueryAsync(IResolveConnectionContext<object?> arg)
    {
        try
        {
            _logger.LogDebug("GraphQL query handling for persisted stream data query started");

            var sessionAccessor = arg.GetSessionAccessor();
            var graphQlUserContext = (GraphQlUserContext)arg.UserContext;
            var tenantId = graphQlUserContext.TenantId;
            var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();

            // Load the persisted stream data query entity
            var queryRtId = arg.GetArgument<OctoObjectId>(Statics.RtIdArg);
            var rtQuery = await tenantRepository.GetRtEntityByRtIdAsync<RtStreamDataSimpleQuery>(
                sessionAccessor.Session, queryRtId);

            if (rtQuery == null)
            {
                throw AssetRepositoryException.RtQueryNotFound(queryRtId);
            }

            // Build the CrateDB query from the persisted entity
            var q = new CrateQueryBuilder(tenantId);
            q.IncludeDefaultVariables();

            // CK type filter
            var ckTypeId = new RtCkId<CkTypeId>(rtQuery.QueryCkTypeId);
            q.WithCkTypeIdFilter(ckTypeId);

            // Resolve column names against the CK model using the central field resolver
            var ckCacheService = arg.GetCkCacheService();
            var requestedType = ckCacheService.GetRtCkType(tenantId, ckTypeId);
            var dataStreamAttributeNames = requestedType.AllAttributes
                .Where(x => x.Value.IsDataStream)
                .Select(x => x.Value.AttributeName);
            var fieldResolver = new StreamDataFieldResolver(dataStreamAttributeNames);

            var columnNames = rtQuery.Columns?.ToList() ?? [];
            var resolvedColumnNames = new List<string>(columnNames.Count);
            foreach (var column in columnNames)
            {
                var resolved = fieldResolver.Resolve(column);
                if (resolved == null)
                {
                    _logger.LogWarning("Stream data column '{Column}' not found in CK model or default fields, skipping", column);
                    continue;
                }

                resolvedColumnNames.Add(resolved.GraphQlAlias);

                if (resolved.Category == StreamDataFieldCategory.Default)
                {
                    // Default fields are already included by IncludeDefaultVariables()
                    continue;
                }

                q.AddVariable(resolved.CrateDbName, resolved.GraphQlAlias, null, true);
            }

            // Execution-time overrides take precedence over persisted defaults
            var execOverride = arg.GetArgument<StreamDataArguments?>(Statics.StreamDataArgument);

            // Time filter: execution override > persisted defaults
            var from = execOverride?.From ?? rtQuery.From;
            var to = execOverride?.To ?? rtQuery.To;

            // Limit: execution override > persisted defaults — stored as rowCap, not set on query builder
            var rowCap = execOverride?.Limit ?? (rtQuery.Limit.HasValue ? (int)rtQuery.Limit.Value : null);

            if (from is not null && to is not null)
            {
                q.WithTimeFilter(from.Value, to.Value);
            }

            // RtId scope filter
            var rtIds = rtQuery.RtIds?.ToList();
            if (rtIds is { Count: > 0 })
            {
                q.AddWhereIn("RtId", rtIds.ToArray());
            }

            // Sorting: runtime override from column header clicks > persisted sorting
            if (arg.TryGetArgument(Statics.SortOrderArg, out IEnumerable<SortDto>? runtimeSortDtos))
            {
                foreach (var sortDto in runtimeSortDtos)
                {
                    var sortOrder = sortDto.SortOrder switch
                    {
                        SortOrdersDto.Descending => SortOrderDto.Descending,
                        _ => SortOrderDto.Ascending
                    };
                    var resolved = fieldResolver.Resolve(sortDto.AttributePath);
                    if (resolved == null)
                    {
                        _logger.LogWarning("Stream data sort field '{AttributePath}' not found in CK model or default fields, skipping", sortDto.AttributePath);
                        continue;
                    }

                    var resolvedSortPath = resolved.Category == StreamDataFieldCategory.Default
                        ? resolved.CrateDbName
                        : resolved.GraphQlAlias;
                    q.OrderBy(resolvedSortPath, sortOrder);
                }
            }
            else
            {
                // Fall back to persisted sorting from query entity
                var sorting = rtQuery.Sorting?.ToList();
                if (sorting is { Count: > 0 })
                {
                    foreach (var sortItem in sorting)
                    {
                        var sortOrder = sortItem.SortOrder switch
                        {
                            RtSortOrdersEnum.Descending => SortOrderDto.Descending,
                            _ => SortOrderDto.Ascending
                        };
                        var resolved = fieldResolver.Resolve(sortItem.AttributePath);
                        if (resolved == null)
                        {
                            _logger.LogWarning("Stream data sort field '{AttributePath}' not found in CK model or default fields, skipping", sortItem.AttributePath);
                            continue;
                        }

                        var resolvedSortPath = resolved.Category == StreamDataFieldCategory.Default
                            ? resolved.CrateDbName
                            : resolved.GraphQlAlias;
                        q.OrderBy(resolvedSortPath, sortOrder);
                    }
                }
            }

            // Field filters - resolve via the central field resolver
            var fieldFilters = rtQuery.FieldFilter?.ToList();
            if (fieldFilters is { Count: > 0 })
            {
                foreach (var filter in fieldFilters)
                {
                    if (filter.ComparisonValue == null)
                    {
                        continue;
                    }

                    var op = MapFieldFilterOperator(filter.Operator);
                    var resolved = fieldResolver.Resolve(filter.AttributePath);
                    // Always use PascalCase CrateDbName for filters; IsDataField determines SQL syntax
                    var resolvedFilterPath = resolved?.CrateDbName ?? filter.AttributePath;
                    var isDataField = resolved?.IsDataField ?? false;

                    q.AddFieldFilter(resolvedFilterPath, op, filter.ComparisonValue, isDataField);
                }
            }

            // Database-level pagination
            var (pagedData, totalCount, effectiveOffset) = await ExecutePaginatedStreamDataQueryAsync(
                q, arg.GetStreamDataDatabaseClient(), tenantId, arg.GetOffset(), arg.First, rowCap);

            _logger.LogDebug("Persisted stream data query executed. Got {Count} rows, totalCount={TotalCount}", pagedData.Count, totalCount);

            var result = pagedData.Select(dp => StreamDataQueryRowDtoType.CreateFromDataPoint(dp, resolvedColumnNames)).ToList();
            return ConnectionUtils.ToOctoConnection(result, arg,
                result.Count != 0 ? effectiveOffset : 0, totalCount);
        }
        catch (Exception e)
        {
            return arg.HandleException(e);
        }
    }

    private async Task<(List<DataPointDto> Data, int TotalCount, int Offset)> ExecutePaginatedStreamDataQueryAsync(
        CrateQueryBuilder q,
        IStreamDataDatabaseClient client,
        string tenantId,
        int? offset,
        int? pageSize,
        int? rowCap)
    {
        var compiler = new CrateQueryCompiler();

        // Compile count query BEFORE setting LIMIT/OFFSET on the query builder
        var countSql = compiler.CompileCountQuery(q);

        // Add Timestamp tiebreaker for deterministic OFFSET-based pagination.
        // Without this, columns with many equal values (e.g., NULLs) cause
        // rows to appear on multiple pages in CrateDB's distributed query execution.
        q.AddOrderByTiebreaker("Timestamp", SortOrderDto.Ascending);

        var effectiveOffset = offset.GetValueOrDefault(0);

        // Compute effective page limit considering rowCap
        int? effectivePageLimit = pageSize;
        if (rowCap.HasValue && effectivePageLimit.HasValue)
        {
            effectivePageLimit = Math.Min(effectivePageLimit.Value, Math.Max(0, rowCap.Value - effectiveOffset));
        }
        else if (rowCap.HasValue)
        {
            effectivePageLimit = Math.Max(0, rowCap.Value - effectiveOffset);
        }

        // Edge case: offset is beyond the row cap — return empty immediately
        if (effectivePageLimit is <= 0)
        {
            return ([], rowCap.GetValueOrDefault(0), effectiveOffset);
        }

        if (effectiveOffset > 0)
        {
            q.WithOffset(effectiveOffset);
        }

        if (effectivePageLimit is > 0)
        {
            q.WithLimit(effectivePageLimit.Value);
        }

        var dataSql = compiler.CompileQuery(q);

        _logger.LogDebug("Executing paginated stream data SQL: {DataSql} | Count: {CountSql}", dataSql, countSql);

        // Execute count + data in parallel
        var countTask = client.GetCountAsync(tenantId, countSql);
        var dataTask = client.GetDataAsync(tenantId, dataSql);
        await Task.WhenAll(countTask, dataTask);

        var totalCount = countTask.Result;
        var effectiveTotalCount = rowCap.HasValue
            ? (int)Math.Min(totalCount, rowCap.Value)
            : (int)totalCount;

        return (dataTask.Result, effectiveTotalCount, effectiveOffset);
    }

    private static StreamDataFieldFilterOperator MapFieldFilterOperator(RtFieldFilterOperatorEnum op)
    {
        return op switch
        {
            RtFieldFilterOperatorEnum.Equals => StreamDataFieldFilterOperator.Equals,
            RtFieldFilterOperatorEnum.NotEquals => StreamDataFieldFilterOperator.NotEquals,
            RtFieldFilterOperatorEnum.LessThan => StreamDataFieldFilterOperator.LessThan,
            RtFieldFilterOperatorEnum.LessEqualThan => StreamDataFieldFilterOperator.LessThanOrEqual,
            RtFieldFilterOperatorEnum.GreaterThan => StreamDataFieldFilterOperator.GreaterThan,
            RtFieldFilterOperatorEnum.GreaterEqualThan => StreamDataFieldFilterOperator.GreaterThanOrEqual,
            RtFieldFilterOperatorEnum.Like => StreamDataFieldFilterOperator.Like,
            _ => throw new ArgumentOutOfRangeException(nameof(op), op,
                $"Field filter operator '{op}' is not supported for stream data queries")
        };
    }
}