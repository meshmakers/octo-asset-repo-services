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
                return ConnectionUtils.ToConnection(new List<RtEntityDto>(), arg);
            }

            var comp = new CrateQueryCompiler();
            var sql = comp.CompileQuery(q);

            _logger.LogDebug("Executing SQL query: {Sql}", sql);

            var streamDataDatabaseClient = arg.GetStreamDataDatabaseClient();

            var data = await streamDataDatabaseClient.GetDataAsync(tenantId, sql);

            _logger.LogDebug("SQL query executed. Got {Count} rows", data.Count);

            var result = data.Select(StreamDataEntityDtoType.CreateStreamDataEntityDto).ToList();

            var offset = arg.GetOffset();
            return ConnectionUtils.ToConnection(result, arg, result.Count != 0 ? offset.GetValueOrDefault(0) : 0,
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
}