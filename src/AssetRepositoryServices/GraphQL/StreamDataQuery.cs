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

            if (entityTimeFilter is { QueryMode: QueryModeDto.Downsampling })
            {
                if (entityTimeFilter.From is null || entityTimeFilter.To is null || entityTimeFilter.Limit is null)
                {
                    throw AssetRepositoryException.InvalidStreamDataQueryParams();
                }

                q.WithDownsampling(entityTimeFilter.Limit.Value, entityTimeFilter.From.Value,
                    entityTimeFilter.To.Value);
            }

            else if (entityTimeFilter is { From: not null, To: not null })
            {
                q.WithTimeFilter(entityTimeFilter.From.Value, entityTimeFilter.To.Value);
            }

            else if (entityTimeFilter is { Limit: not null })
            {
                q.WithLimit(entityTimeFilter.Limit.Value);
            }

            HandleRequestedAttributes(fieldContext, requestedType, q);

            if (!HandleRequestedRtIds(arg, q))
                // we got an empty array of rtIds so we return an empty connection
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

            var offset = arg.GetOffset();
            return ConnectionUtils.ToOctoConnection(result, arg, result.Count != 0 ? offset.GetValueOrDefault(0) : 0,
                result.Count);
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

            // Resolve column names against the CK model to get the correct casing
            // (GraphQL camelCases attribute names, but CrateDB uses the original CK attribute names)
            var ckCacheService = arg.GetCkCacheService();
            var requestedType = ckCacheService.GetRtCkType(tenantId, ckTypeId);
            var dataStreamAttributes = requestedType.AllAttributes
                .Where(x => x.Value.IsDataStream)
                .ToList();

            var columnNames = rtQuery.Columns?.ToList() ?? [];
            var resolvedColumnNames = new List<string>(columnNames.Count);
            foreach (var column in columnNames)
            {
                // Skip default fields (RtId, CkTypeId, Timestamp, etc.) - already included by IncludeDefaultVariables()
                var standardField = Constants.DefaultStreamDataFields.FirstOrDefault(x =>
                    string.Equals(x, column, StringComparison.InvariantCultureIgnoreCase));
                if (standardField != null)
                {
                    resolvedColumnNames.Add(standardField.ToCamelCase());
                    continue;
                }

                // Resolve data stream attribute names: PascalCase for CrateDB access, camelCase for alias + resolvedColumnNames
                var matchedAttribute = dataStreamAttributes
                    .FirstOrDefault(x => string.Equals(x.Value.AttributeName, column,
                        StringComparison.InvariantCultureIgnoreCase));

                var resolvedName = matchedAttribute.Value?.AttributeName ?? column;
                var camelCaseName = resolvedName.ToCamelCase();
                resolvedColumnNames.Add(camelCaseName);
                q.AddVariable(resolvedName, camelCaseName, null, true);
            }

            // Execution-time overrides take precedence over persisted defaults
            var execOverride = arg.GetArgument<StreamDataArguments?>(Statics.StreamDataArgument);

            // Query mode (from persisted query)
            var queryMode = rtQuery.QueryMode;
            var isDownsampling = queryMode is RtStreamDataQueryModesEnum.Downsampling
                                 || execOverride is { QueryMode: QueryModeDto.Downsampling };

            // Time filter: execution override > persisted defaults
            var from = execOverride?.From ?? rtQuery.From;
            var to = execOverride?.To ?? rtQuery.To;

            // Limit: execution override > persisted defaults
            var limit = execOverride?.Limit ?? (rtQuery.Limit.HasValue ? (int)rtQuery.Limit.Value : null);

            if (isDownsampling)
            {
                if (from is null || to is null || limit is null)
                {
                    throw AssetRepositoryException.InvalidStreamDataQueryParams();
                }

                q.WithDownsampling(limit.Value, from.Value, to.Value);
            }
            else
            {
                if (from is not null && to is not null)
                {
                    q.WithTimeFilter(from.Value, to.Value);
                }

                if (limit is not null)
                {
                    q.WithLimit(limit.Value);
                }
            }

            // RtId scope filter
            var rtIds = rtQuery.RtIds?.ToList();
            if (rtIds is { Count: > 0 })
            {
                q.AddWhereIn("RtId", rtIds.ToArray());
            }

            // Sorting - resolve attribute paths to correct CK casing
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
                    var matchedSortAttr = dataStreamAttributes
                        .FirstOrDefault(x => string.Equals(x.Value.AttributeName, sortItem.AttributePath,
                            StringComparison.InvariantCultureIgnoreCase));
                    var resolvedSortPath = matchedSortAttr.Value?.AttributeName.ToCamelCase() ?? sortItem.AttributePath;
                    q.OrderBy(resolvedSortPath, sortOrder);
                }
            }

            // Field filters
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
                    // Check if the field is a data column (in the selected columns list) or a standard field
                    // Use case-insensitive matching since filter paths may also be camelCased
                    var isDataField = resolvedColumnNames.Any(c =>
                        string.Equals(c, filter.AttributePath, StringComparison.InvariantCultureIgnoreCase));

                    // Resolve the filter attribute path to the correct CK casing
                    var resolvedFilterPath = filter.AttributePath;
                    if (isDataField)
                    {
                        var matchedFilterAttr = dataStreamAttributes
                            .FirstOrDefault(x => string.Equals(x.Value.AttributeName, filter.AttributePath,
                                StringComparison.InvariantCultureIgnoreCase));
                        resolvedFilterPath = matchedFilterAttr.Value?.AttributeName.ToCamelCase() ?? filter.AttributePath;
                    }

                    q.AddFieldFilter(resolvedFilterPath, op, filter.ComparisonValue, isDataField);
                }
            }

            // Compile and execute
            var compiler = new CrateQueryCompiler();
            var sql = compiler.CompileQuery(q);

            _logger.LogDebug("Executing persisted stream data SQL query: {Sql}", sql);

            var streamDataDatabaseClient = arg.GetStreamDataDatabaseClient();
            var data = await streamDataDatabaseClient.GetDataAsync(tenantId, sql);

            _logger.LogDebug("Persisted stream data query executed. Got {Count} rows", data.Count);

            var result = data.Select(dp => StreamDataQueryRowDtoType.CreateFromDataPoint(dp, resolvedColumnNames)).ToList();

            var offset = arg.GetOffset();
            return ConnectionUtils.ToOctoConnection(result, arg,
                result.Count != 0 ? offset.GetValueOrDefault(0) : 0, result.Count);
        }
        catch (Exception e)
        {
            return arg.HandleException(e);
        }
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