using GraphQL;
using GraphQL.Builders;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using NLog;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

internal sealed class CkQuery : ObjectGraphType
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public CkQuery()
    {
        Name = "ConstructionKit";

        Connection<CkTypeDtoType>("Types")
            .Argument<StringGraphType>(Statics.CkIdArg, "Returns the construction kit type with the given id.")
            .Argument<ListGraphType<StringGraphType>>(Statics.CkIdsArg,
                "Returns the construction kit types with the given ids.")
            .Argument<SearchFilterDtoType>(Statics.SearchFilterArg, "Filters items based on text search")
            .Argument<ListGraphType<SortDtoType>>(Statics.SortOrderArg, "Sort order for items")
            .Argument<ListGraphType<FieldFilterDtoType>>(Statics.FieldFilterArg,
                "Filters items based on field compare")
            .ResolveAsync(ResolveCkTypesQuery);
        
        Connection<CkAttributeDtoType>("Attributes")
            .Argument<StringGraphType>(Statics.CkIdArg, "Returns the entity with the given attribute id.")
            .Argument<ListGraphType<StringGraphType>>(Statics.CkIdsArg,
                "Returns entities with the given attribute ids.")
            .Argument<SearchFilterDtoType>(Statics.SearchFilterArg, "Filters items based on text search")
            .Argument<ListGraphType<SortDtoType>>(Statics.SortOrderArg, "Sort order for items")
            .Argument<ListGraphType<FieldFilterDtoType>>(Statics.FieldFilterArg,
                "Filters items based on field compare")
            .ResolveAsync(ResolveCkAttributesQuery);
        
        Connection<CkEnumDtoType>("Enums")
            .Argument<StringGraphType>(Statics.CkIdArg, "Returns the enum with the given enum id.")
            .Argument<ListGraphType<StringGraphType>>(Statics.CkIdsArg,
                "Returns enums with the given enum ids.")
            .Argument<SearchFilterDtoType>(Statics.SearchFilterArg, "Filters items based on text search")
            .Argument<ListGraphType<SortDtoType>>(Statics.SortOrderArg, "Sort order for items")
            .Argument<ListGraphType<FieldFilterDtoType>>(Statics.FieldFilterArg,
                "Filters items based on field compare")
            .ResolveAsync(ResolveCkEnumQuery);
        
        Connection<CkRecordDtoType>("Records")
            .Argument<StringGraphType>(Statics.CkIdArg, "Returns the record with the given record id.")
            .Argument<ListGraphType<StringGraphType>>(Statics.CkIdsArg,
                "Returns records with the given record ids.")
            .Argument<SearchFilterDtoType>(Statics.SearchFilterArg, "Filters items based on text search")
            .Argument<ListGraphType<SortDtoType>>(Statics.SortOrderArg, "Sort order for items")
            .Argument<ListGraphType<FieldFilterDtoType>>(Statics.FieldFilterArg,
                "Filters items based on field compare")
            .ResolveAsync(ResolveCkRecordQuery);
    }

    private async Task<object?> ResolveCkRecordQuery(IResolveConnectionContext<object?> arg)
    {
        Logger.Debug("GraphQL query handling of contruction kit records started");

        var sessionAccessor = arg.RequestServices?.GetRequiredService<IOctoSessionAccessor>();
        if (sessionAccessor?.Session == null)
        {
            throw AssetRepositoryException.SessionUnavailable();
        }

        var graphQlUserContext = (GraphQlUserContext)arg.UserContext;

        var offset = arg.GetOffset();
        var dataQueryOperation = arg.GetDataQueryOperation();

        var keysList = new List<CkId<CkRecordId>>();
        if (arg.TryGetArgument(Statics.CkIdArg, out string? key))
        {
            keysList.Add(key);
        }

        if (arg.TryGetArgument(Statics.CkIdsArg, null, out IEnumerable<string>? keys))
        {
            keysList.AddRange(keys.Select(k => new CkId<CkRecordId>(k)));
        }

        // if argument defined, but empty array, do not return any data. That mus be a mistake by client (otherwise
        // all entities are returned.
        if (!keysList.Any() && (arg.HasArgument(Statics.CkIdArg) || arg.HasArgument(Statics.CkIdsArg)))
        {
            return ConnectionUtils.ToConnection(new List<CkRecordDto>(), arg, null);
        }

        var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();
        var resultSet =
            await tenantRepository.GetCkRecordAsync(sessionAccessor.Session,
                keysList, dataQueryOperation, offset, arg.First);

        Logger.Debug("GraphQL query handling returning data for contruction kit records");
        return ConnectionUtils.ToConnection(resultSet.Items.Select(CkRecordDtoType.CreateCkRecordDto), arg,
            resultSet.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSet.TotalCount, resultSet.Grouping);
    }

    private async Task<object?> ResolveCkEnumQuery(IResolveConnectionContext<object?> arg)
    {
        Logger.Debug("GraphQL query handling of contruction kit enums started");

        var sessionAccessor = arg.RequestServices?.GetRequiredService<IOctoSessionAccessor>();
        if (sessionAccessor?.Session == null)
        {
            throw AssetRepositoryException.SessionUnavailable();
        }

        var graphQlUserContext = (GraphQlUserContext)arg.UserContext;

        var offset = arg.GetOffset();
        var dataQueryOperation = arg.GetDataQueryOperation();

        var keysList = new List<CkId<CkEnumId>>();
        if (arg.TryGetArgument(Statics.CkIdArg, out string? key))
        {
            keysList.Add(key);
        }

        if (arg.TryGetArgument(Statics.CkIdsArg, null, out IEnumerable<string>? keys))
        {
            keysList.AddRange(keys.Select(k => new CkId<CkEnumId>(k)));
        }

        // if argument defined, but empty array, do not return any data. That mus be a mistake by client (otherwise
        // all entities are returned.
        if (!keysList.Any() && (arg.HasArgument(Statics.CkIdArg) || arg.HasArgument(Statics.CkIdsArg)))
        {
            return ConnectionUtils.ToConnection(new List<CkEnumDto>(), arg, null);
        }

        var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();
        var resultSet =
            await tenantRepository.GetCkEnumAsync(sessionAccessor.Session,
                keysList, dataQueryOperation, offset, arg.First);

        Logger.Debug("GraphQL query handling returning data for contruction kit eums");
        return ConnectionUtils.ToConnection(resultSet.Items.Select(CkEnumDtoType.CreateCkEnumDto), arg,
            resultSet.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSet.TotalCount, resultSet.Grouping);
    }

    private async Task<object?> ResolveCkTypesQuery(IResolveConnectionContext<object?> arg)
    {
        Logger.Debug("GraphQL query handling of contruction kit entities started");

        var sessionAccessor = arg.RequestServices?.GetRequiredService<IOctoSessionAccessor>();
        if (sessionAccessor?.Session == null)
        {
            throw AssetRepositoryException.SessionUnavailable();
        }

        var graphQlUserContext = (GraphQlUserContext)arg.UserContext;

        var offset = arg.GetOffset();
        var dataQueryOperation = arg.GetDataQueryOperation();

        var keysList = new List<CkId<CkTypeId>>();
        if (arg.TryGetArgument(Statics.CkIdArg, out string? key))
        {
            keysList.Add(key);
        }

        if (arg.TryGetArgument(Statics.CkIdsArg, null, out IEnumerable<string>? keys))
        {
            keysList.AddRange(keys.Select(k => new CkId<CkTypeId>(k)));
        }

        // if argument defined, but empty array, do not return any data. That mus be a mistake by client (otherwise
        // all entities are returned.
        if (!keysList.Any() && (arg.HasArgument(Statics.CkIdArg) || arg.HasArgument(Statics.CkIdsArg)))
        {
            return ConnectionUtils.ToConnection(new List<CkTypeDto>(), arg, null);
        }

        var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();
        var resultSet =
            await tenantRepository.GetCkTypeAsync(sessionAccessor.Session,
                keysList, dataQueryOperation, offset, arg.First);

        Logger.Debug("GraphQL query handling returning data for contruction kit entities");
        return ConnectionUtils.ToConnection(resultSet.Items.Select(CkTypeDtoType.CreateCkTypeDto), arg,
            resultSet.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSet.TotalCount, resultSet.Grouping);
    }
    
    private async Task<object?> ResolveCkAttributesQuery(IResolveConnectionContext<object?> arg)
    {
        Logger.Debug("GraphQL query handling of contruction kit attributes started");
        
        var sessionAccessor = arg.RequestServices?.GetRequiredService<IOctoSessionAccessor>();
        if (sessionAccessor?.Session == null)
        {
            throw AssetRepositoryException.SessionUnavailable();
        }

        var graphQlUserContext = (GraphQlUserContext)arg.UserContext;

        var offset = arg.GetOffset();
        var dataQueryOperation = arg.GetDataQueryOperation();

        var keysList = new List<CkId<CkAttributeId>>();
        if (arg.TryGetArgument(Statics.CkIdArg, out string? key))
        {
            keysList.Add(new CkId<CkAttributeId>(key));
        }

        if (arg.TryGetArgument(Statics.CkIdsArg, null, out IEnumerable<string>? keys))
        {
            keysList.AddRange(keys.Select(k => new CkId<CkAttributeId>(k)));
        }

        // if argument defined, but empty array, do not return any data. That mus be a mistake by client (otherwise
        // all entities are returned.
        if (!keysList.Any() && (arg.HasArgument(Statics.CkIdArg) || arg.HasArgument(Statics.CkIdsArg)))
        {
            return ConnectionUtils.ToConnection(new List<RtEntityDto>(), arg, null);
        }

        var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();
        var resultSet =
            await tenantRepository.GetCkAttributesAsync(sessionAccessor.Session,
                keysList, dataQueryOperation, offset, arg.First);

        Logger.Debug("GraphQL query handling returning data for contruction kit attributes");
        return ConnectionUtils.ToConnection(resultSet.Items.Select(CkAttributeDtoType.CreateCkAttributeDto), arg,
            resultSet.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSet.TotalCount, null);
    }
}