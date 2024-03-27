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
using NLog;
using SqlKata;
using SqlKata.Compilers;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

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
                .Argument<TimeFilterGraphType>(Statics.TimeSeriesFilterArg, "Filter for time series data.")
                .Argument<ListGraphType<SortDtoType>>(Statics.SortOrderArg, "Sort order for items")
                .ResolveAsync(ResolveRtEntitiesQuery);
        }
    }

    private async Task<object?> ResolveRtEntitiesQuery(IResolveConnectionContext<object?> arg)
    {
        Logger.Debug("GraphQL query handling for specific runtime entity type started");

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
        
        var tsQuery = new Query(tenantId);

        var entityTimeFilter = fieldContext.GetArgument<EntityTimeFilterDto>(Statics.TimeSeriesFilterArg);

        if (entityTimeFilter is not null)
        {
            tsQuery.WhereBetween(Constants.Timestamp, entityTimeFilter.From, entityTimeFilter.To);
        }

        HandleRequestedAttributes(fieldContext, requestedType, tsQuery);
   


        // add standard fields 
        tsQuery.Select(Constants.CkTypeId);
        tsQuery.Select(Constants.Timestamp);
        tsQuery.Select(Constants.RtId);


        if (!HandleRequestedRtIds(arg, tsQuery))
        {
            return ConnectionUtils.ToConnection(new List<RtEntityDto>(), arg, null);
        }


        var compiler = new PostgresCompiler();
        var compiled = compiler.Compile(tsQuery);
        if (compiled == null)
        {
            return null;
        }

        var sql = compiled.ToString();
        var tsClient = services.GetRequiredService<ITimeSeriesDatabaseClient>()!;
        var data = await tsClient.GetDataAsync(sql);

        var result = data.Select(TsEntityDtoType.CreateTsEntityDto).ToList();
        
        var offset = arg.GetOffset();
        return ConnectionUtils.ToConnection(result, arg, result.Count != 0 ? offset.GetValueOrDefault(0) : 0,
            result.Count, []);
    }

    private bool HandleRequestedRtIds(IResolveConnectionContext<object?> arg, Query tsQuery)
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
            tsQuery.WhereIn(Constants.RtId, rtIdList.Select(x => x.ToString()));
        }

        return true;
    }

    private static void HandleRequestedAttributes(FieldContext fieldContext, CkTypeGraph requestedType, Query tsQuery)
    {
        var items = fieldContext.Fields.FirstOrDefault(x => x.Name == Statics.ItemsQueryArg);
        if (items == null)
        {
            return;
        }
        
        foreach (var (_, attribute) in requestedType.AllAttributes.Where(x => x.Value.IsDataStream))
        {
            bool SelectionContainsAttribute(FieldContext x) => string.Equals(x.Name,
                attribute.AttributeName, StringComparison.InvariantCultureIgnoreCase);

            if (items.Fields.Any(SelectionContainsAttribute))
            {
                // we want data from the data field
                tsQuery.Select($"data['{attribute.AttributeName}'] as {attribute.AttributeName}");
            }
            else if (Constants.DefaultTimeSeriesFields.Any(x =>
                         string.Equals(attribute.AttributeName, x, StringComparison.InvariantCultureIgnoreCase)))
            {
                tsQuery.Select(attribute.AttributeName);
            }
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