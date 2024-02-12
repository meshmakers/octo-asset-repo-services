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
using NLog;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

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
                .Argument<ListGraphType<FieldFilterDtoType>>(Statics.FieldFilterArg,
                    "Filters items based on field compare")
               .ResolveAsync(ResolveRtEntitiesQuery);
        }
    }

    private async Task<object?> ResolveGenericRtEntitiesQuery(IResolveConnectionContext<object?> arg)
    {
        Logger.Debug("GraphQL query handling for generic runtime entity started");
        
        var sessionAccessor = arg.RequestServices?.GetRequiredService<IOctoSessionAccessor>();
        if (sessionAccessor == null)
        {
            throw AssetRepositoryException.ServiceNotRegistered(typeof(IOctoSessionAccessor));
        }

        var graphQlUserContext = (GraphQlUserContext)arg.UserContext;
        var ckId = arg.GetArgument<string>(Statics.CkIdArg);

        var offset = arg.GetOffset();
        var dataQueryOperation = arg.GetDataQueryOperation();

        var keysList = new List<OctoObjectId>();
        if (arg.TryGetArgument(Statics.RtIdArg, out string? key))
        {
            keysList.Add(new OctoObjectId(key));
        }

        if (arg.TryGetArgument(Statics.RtIdsArg, null, out IEnumerable<string>? keys))
        {
            keysList.AddRange(keys.Select(k => new OctoObjectId(k)));
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
                    sessionAccessor.Session, ckId, keysList, dataQueryOperation,
                    offset, arg.First);

            Logger.Debug("GraphQL query handling returning data by keys");
            return ConnectionUtils.ToConnection(resultSetIds.Items.Select(RtEntityDtoType.CreateRtEntityDto), arg,
                resultSetIds.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSetIds.TotalCount, resultSetIds.Grouping);
        }

        var resultSet =
            await tenantRepository.GetRtEntitiesByTypeAsync(sessionAccessor.Session,
                ckId, dataQueryOperation, offset,
                arg.First);

        Logger.Debug("GraphQL query handling returning data");
        return ConnectionUtils.ToConnection(resultSet.Items.Select(RtEntityDtoType.CreateRtEntityDto), arg,
            resultSet.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSet.TotalCount, resultSet.Grouping);
    }
    
    private async Task<object?> ResolveRtEntitiesQuery(IResolveConnectionContext<object?> arg)
    {
        Logger.Debug("GraphQL query handling for specific runtime entity type started");
        
        var sessionAccessor = arg.RequestServices?.GetRequiredService<IOctoSessionAccessor>();
        if (sessionAccessor == null)
        {
            throw AssetRepositoryException.ServiceNotRegistered(typeof(IOctoSessionAccessor));
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
        if (arg.TryGetArgument(Statics.RtIdArg, out string? key))
        {
            keysList.Add(new OctoObjectId(key));
        }

        if (arg.TryGetArgument(Statics.RtIdsArg, null, out IEnumerable<string>? keys))
        {
            keysList.AddRange(keys.Select(k => new OctoObjectId(k)));
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
                resultSetIds.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSetIds.TotalCount, resultSetIds.Grouping);
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