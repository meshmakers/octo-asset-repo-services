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
using Meshmakers.Octo.Services.Common.Timeseries;
using Meshmakers.Octo.Services.Common.Timeseries.Dtos;
using Meshmakers.Octo.Services.Common.Timeseries.QueryBuilder;
using NLog;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

[DoNotRegister]
internal sealed class TsQuery : ObjectGraphType
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public TsQuery(IGraphTypesCache graphTypesCache)
    {
        Name = "TimeseriesModelQuery";

        foreach (var rtEntityDtoType in graphTypesCache.GetStreamTypes())
        {
            this.Connection<object?, IGraphType, TsEntityDto>(graphTypesCache, rtEntityDtoType, rtEntityDtoType.Name)
                .AddMetadata(Statics.CkId, rtEntityDtoType.CkTypeId)
                .Argument<OctoObjectIdType>(Statics.RtIdArg, "Returns the entity with the given rtId.")
                .Argument<ListGraphType<OctoObjectIdType>>(Statics.RtIdsArg, "Returns entities with the given rtIds.")
                .Argument<EntityTimeFilterGraphType>(Statics.TimeSeriesFilterArg, "Filter for time series data.")
                .Argument<ListGraphType<SortDtoType>>(Statics.SortOrderArg, "Sort order for items")
                .ResolveAsync(ResolveRtEntitiesQuery);
        }
    }

    private async Task<object?> ResolveRtEntitiesQuery(IResolveConnectionContext<object?> arg)
    {
        Logger.Debug("GraphQL query handling for specific timeseries entity type started");

        var fieldContext = FieldContext.FromContext(arg);

        var services = arg.RequestServices;
        if (services == null)
        {
            throw AssetRepositoryException.ServiceNotRegistered(typeof(IServiceProvider));
        }

        var typeCache = services.GetRequiredService<ICkCacheService>();
        var ckTypeId = TryGetCkTypeId(arg);
        if (ckTypeId == null)
        {
            throw AssetRepositoryException.CkIdMetadataMissing();
        }

        var graphQlUserContext = (GraphQlUserContext)arg.UserContext;
        var tenantId = graphQlUserContext.TenantId;
        var requestedType = typeCache.GetCkType(tenantId, ckTypeId);

        var q = new CrateQueryBuilder(tenantId);

        var entityTimeFilter = fieldContext.GetArgument<EntityTimeFilterDto>(Statics.TimeSeriesFilterArg);

        if (entityTimeFilter is not null)
        {
            q.WithTimeFilter(entityTimeFilter.From, entityTimeFilter.To);
        }

        HandleRequestedAttributes(fieldContext, requestedType, q);

        if (!HandleRequestedRtIds(arg))
        {
            // we got an empty array of rtIds so we return a empty connection
            return ConnectionUtils.ToConnection(new List<RtEntityDto>(), arg, null);
        }

        var comp = new CrateQueryCompiler();
        var sql = comp.CompileQuery(q);

        Logger.Debug("Executing SQL query: {0}", sql);

        var tsClient = services.GetRequiredService<ITimeSeriesDatabaseClient>();
        var data = await tsClient.GetDataAsync(sql);

        Logger.Debug("SQL query executed. Got {0} rows", data.Count());

        var result = data.Select(TsEntityDtoType.CreateTsEntityDto).ToList();

        var offset = arg.GetOffset();
        return ConnectionUtils.ToConnection(result, arg, result.Count != 0 ? offset.GetValueOrDefault(0) : 0,
            result.Count, []);
    }

    private bool HandleRequestedRtIds(IResolveConnectionContext<object?> arg)
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
            throw new NotImplementedException();
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
            var argument = field.GetArgument<AttributeTsArgumentDto>(Statics.TimeSeriesAttributeArgument);

            bool ContainsField(KeyValuePair<CkId<CkAttributeId>, CkTypeAttributeGraph> x) =>
                string.Equals(x.Value.AttributeName, field.Name, StringComparison.InvariantCultureIgnoreCase);

            var (_, requestedAttribute) = dataStreamAttributes
                .FirstOrDefault(ContainsField);

            if (requestedAttribute != null)
            {
                AddVariable(requestedAttribute.AttributeName, argument, true, q);
                if (argument?.SortPriority != null)
                {
                    orderQueue.Enqueue(new Tuple<string, SortOrderDto>(requestedAttribute.AttributeName, argument.SortOrder.GetValueOrDefault(SortOrderDto.Ascending)),
                        argument.SortPriority.GetValueOrDefault(0));
                }
            }


            var standardField = Constants.DefaultTimeSeriesFields.FirstOrDefault(x =>
                string.Equals(x, field.Name, StringComparison.InvariantCultureIgnoreCase));
            if (standardField == null)
            {
                continue;
            }

            AddVariable(standardField, argument, false, q);
        }
        
        
        while(orderQueue.Count > 0)
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
            q.AddVariable(name, name, null, isDataVariable);
        }
    }

    private static CkId<CkTypeId>? TryGetCkTypeId(IResolveConnectionContext<object?> arg)
    {
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

        return ckTypeId;
    }
}