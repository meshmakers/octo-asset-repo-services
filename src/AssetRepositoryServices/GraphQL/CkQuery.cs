using GraphQL;
using GraphQL.Builders;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

internal sealed class CkQuery : ObjectGraphType
{
    private readonly ILogger<CkQuery> _logger;

    public CkQuery(ILogger<CkQuery> logger)
    {
        _logger = logger;
        Name = "ConstructionKitQuery";

        Connection<CkModelDtoType>("Models")
            .Argument<StringGraphType>(Statics.CkIdArg, "Returns the construction kit model with the given id.")
            .Argument<ListGraphType<StringGraphType>>(Statics.CkIdsArg,
                "Returns the construction kit models with the given ids.")
            .Argument<SearchFilterDtoType>(Statics.SearchFilterArg, "Filters items based on text search")
            .Argument<ListGraphType<SortDtoType>>(Statics.SortOrderArg, "Sort order for items")
            .Argument<ListGraphType<FieldFilterDtoType>>(Statics.FieldFilterArg,
                "Filters items based on field compare")
            .ResolveAsync(ResolveCkModelsQuery);

        Connection<CkTypeDtoType>("Types")
            .Argument<ListGraphType<StringGraphType>>(Statics.CkModelIds, "Filters items based on model ids")
            .Argument<StringGraphType>(Statics.CkIdArg, "Returns the construction kit type with the given id.")
            .Argument<ListGraphType<StringGraphType>>(Statics.CkIdsArg,
                "Returns the construction kit types with the given ids.")
            .Argument<SearchFilterDtoType>(Statics.SearchFilterArg, "Filters items based on text search")
            .Argument<ListGraphType<SortDtoType>>(Statics.SortOrderArg, "Sort order for items")
            .Argument<ListGraphType<FieldFilterDtoType>>(Statics.FieldFilterArg,
                "Filters items based on field compare")
            .ResolveAsync(ResolveCkTypesQuery);

        Connection<CkAttributeDtoType>("Attributes")
            .Argument<ListGraphType<StringGraphType>>(Statics.CkModelIds, "Filters items based on model ids")
            .Argument<StringGraphType>(Statics.CkIdArg, "Returns the entity with the given attribute id.")
            .Argument<ListGraphType<StringGraphType>>(Statics.CkIdsArg,
                "Returns entities with the given attribute ids.")
            .Argument<SearchFilterDtoType>(Statics.SearchFilterArg, "Filters items based on text search")
            .Argument<ListGraphType<SortDtoType>>(Statics.SortOrderArg, "Sort order for items")
            .Argument<ListGraphType<FieldFilterDtoType>>(Statics.FieldFilterArg,
                "Filters items based on field compare")
            .ResolveAsync(ResolveCkAttributesQuery);

        Connection<CkEnumDtoType>("Enums")
            .Argument<ListGraphType<StringGraphType>>(Statics.CkModelIds, "Filters items based on model ids")
            .Argument<StringGraphType>(Statics.CkIdArg, "Returns the enum with the given enum id.")
            .Argument<ListGraphType<StringGraphType>>(Statics.CkIdsArg,
                "Returns enums with the given enum ids.")
            .Argument<SearchFilterDtoType>(Statics.SearchFilterArg, "Filters items based on text search")
            .Argument<ListGraphType<SortDtoType>>(Statics.SortOrderArg, "Sort order for items")
            .Argument<ListGraphType<FieldFilterDtoType>>(Statics.FieldFilterArg,
                "Filters items based on field compare")
            .ResolveAsync(ResolveCkEnumQuery);

        Connection<CkRecordDtoType>("Records")
            .Argument<ListGraphType<StringGraphType>>(Statics.CkModelIds, "Filters items based on model ids")
            .Argument<StringGraphType>(Statics.CkIdArg, "Returns the record with the given record id.")
            .Argument<ListGraphType<StringGraphType>>(Statics.CkIdsArg,
                "Returns records with the given record ids.")
            .Argument<SearchFilterDtoType>(Statics.SearchFilterArg, "Filters items based on text search")
            .Argument<ListGraphType<SortDtoType>>(Statics.SortOrderArg, "Sort order for items")
            .Argument<ListGraphType<FieldFilterDtoType>>(Statics.FieldFilterArg,
                "Filters items based on field compare")
            .ResolveAsync(ResolveCkRecordQuery);
    }

    private async Task<object?> ResolveCkModelsQuery(IResolveConnectionContext<object?> arg)
    {
        try
        {
            _logger.LogDebug("GraphQL query handling of construction kit models started");

            var sessionAccessor = arg.GetSessionAccessor();

            var graphQlUserContext = (GraphQlUserContext)arg.UserContext;

            var offset = arg.GetOffset();
            var dataQueryOperation = arg.GetDataQueryOperation();

            var keysList = new List<CkModelId>();
            if (arg.TryGetArgument(Statics.CkIdArg, out string? key))
            {
                keysList.Add(new CkModelId(key));
            }

            if (arg.TryGetArgument(Statics.CkIdsArg, null, out IEnumerable<string>? keys))
            {
                keysList.AddRange(keys.Select(k => new CkModelId(k)));
            }

            // If argument defined, but empty array, do not return any data. That must be a mistake by client (otherwise
            // all entities are returned)
            if (!keysList.Any() && (arg.HasArgument(Statics.CkIdArg) || arg.HasArgument(Statics.CkIdsArg)))
            {
                return ConnectionUtils.ToConnection(new List<CkModelDto>(), arg);
            }

            var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();
            var resultSet =
                await tenantRepository.GetCkModelsAsync(sessionAccessor.Session,
                    keysList, dataQueryOperation, offset, arg.First);

            _logger.LogDebug("GraphQL query handling returning data for construction kit models");
            return ConnectionUtils.ToConnection(resultSet.Items.Select(CkModelDtoType.CreateCkModelDto), arg,
                resultSet.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSet.TotalCount,
                resultSet.AggregationResult, resultSet.FieldAggregationResult);
        }
        catch (Exception e)
        {
            return arg.HandleException(e);
        }
    }

    private async Task<object?> ResolveCkRecordQuery(IResolveConnectionContext<object?> arg)
    {
        try
        {
            _logger.LogDebug("GraphQL query handling of construction kit records started");

            var sessionAccessor = arg.GetSessionAccessor();

            var graphQlUserContext = (GraphQlUserContext)arg.UserContext;

            var offset = arg.GetOffset();
            var dataQueryOperation = arg.GetDataQueryOperation();

            var modelIdList = new List<CkModelId>();
            if (arg.TryGetArgument(Statics.CkModelIds, null, out IEnumerable<string>? modelIds))
            {
                modelIdList.AddRange(modelIds.Select(k => new CkModelId(k)));
            }

            var keysList = new List<CkId<CkRecordId>>();
            if (arg.TryGetArgument(Statics.CkIdArg, out string? key))
            {
                keysList.Add(new CkId<CkRecordId>(key));
            }

            if (arg.TryGetArgument(Statics.CkIdsArg, null, out IEnumerable<string>? keys))
            {
                keysList.AddRange(keys.Select(k => new CkId<CkRecordId>(k)));
            }

            // If argument defined, but empty array, do not return any data. That must be a mistake by client (otherwise
            // all entities are returned)
            if (!keysList.Any() && (arg.HasArgument(Statics.CkIdArg) || arg.HasArgument(Statics.CkIdsArg)))
            {
                return ConnectionUtils.ToConnection(new List<CkRecordDto>(), arg);
            }

            var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();
            var resultSet =
                await tenantRepository.GetCkRecordAsync(sessionAccessor.Session, modelIdList,
                    keysList, dataQueryOperation, offset, arg.First);

            _logger.LogDebug("GraphQL query handling returning data for construction kit records");
            return ConnectionUtils.ToConnection(resultSet.Items.Select(CkRecordDtoType.CreateCkRecordDto), arg,
                resultSet.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSet.TotalCount,
                resultSet.AggregationResult, resultSet.FieldAggregationResult);
        }
        catch (Exception e)
        {
            return arg.HandleException(e);
        }
    }

    private async Task<object?> ResolveCkEnumQuery(IResolveConnectionContext<object?> arg)
    {
        try
        {
            _logger.LogDebug("GraphQL query handling of construction kit enums started");

            var sessionAccessor = arg.GetSessionAccessor();
            var graphQlUserContext = (GraphQlUserContext)arg.UserContext;

            var offset = arg.GetOffset();
            var dataQueryOperation = arg.GetDataQueryOperation();

            var modelIdList = new List<CkModelId>();
            if (arg.TryGetArgument(Statics.CkModelIds, null, out IEnumerable<string>? modelIds))
            {
                modelIdList.AddRange(modelIds.Select(k => new CkModelId(k)));
            }

            var keysList = new List<CkId<CkEnumId>>();
            if (arg.TryGetArgument(Statics.CkIdArg, out string? key))
            {
                keysList.Add(new CkId<CkEnumId>(key));
            }

            if (arg.TryGetArgument(Statics.CkIdsArg, null, out IEnumerable<string>? keys))
            {
                keysList.AddRange(keys.Select(k => new CkId<CkEnumId>(k)));
            }

            // If argument defined, but empty array, do not return any data. That must be a mistake by client (otherwise
            // all entities are returned)
            if (!keysList.Any() && (arg.HasArgument(Statics.CkIdArg) || arg.HasArgument(Statics.CkIdsArg)))
            {
                return ConnectionUtils.ToConnection(new List<CkEnumDto>(), arg);
            }

            var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();
            var resultSet =
                await tenantRepository.GetCkEnumAsync(sessionAccessor.Session, modelIdList,
                    keysList, dataQueryOperation, offset, arg.First);

            _logger.LogDebug("GraphQL query handling returning data for construction kit enums");
            return ConnectionUtils.ToConnection(resultSet.Items.Select(CkEnumDtoType.CreateCkEnumDto), arg,
                resultSet.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSet.TotalCount,
                resultSet.AggregationResult, resultSet.FieldAggregationResult);
        }
        catch (Exception e)
        {
            return arg.HandleException(e);
        }
    }

    private async Task<object?> ResolveCkTypesQuery(IResolveConnectionContext<object?> arg)
    {
        try
        {
            _logger.LogDebug("GraphQL query handling of construction kit entities started");

            var sessionAccessor = arg.GetSessionAccessor();
            var graphQlUserContext = (GraphQlUserContext)arg.UserContext;

            var offset = arg.GetOffset();
            var dataQueryOperation = arg.GetDataQueryOperation();

            var modelIdList = new List<CkModelId>();
            if (arg.TryGetArgument(Statics.CkModelIds, null, out IEnumerable<string>? modelIds))
            {
                modelIdList.AddRange(modelIds.Select(k => new CkModelId(k)));
            }

            var keysList = new List<CkId<CkTypeId>>();
            if (arg.TryGetArgument(Statics.CkIdArg, out string? key))
            {
                keysList.Add(new CkId<CkTypeId>(key));
            }

            if (arg.TryGetArgument(Statics.CkIdsArg, null, out IEnumerable<string>? keys))
            {
                keysList.AddRange(keys.Select(k => new CkId<CkTypeId>(k)));
            }

            // If argument defined, but empty array, do not return any data. That must be a mistake by client (otherwise
            // all entities are returned)
            if (!keysList.Any() && (arg.HasArgument(Statics.CkIdArg) || arg.HasArgument(Statics.CkIdsArg)))
            {
                return ConnectionUtils.ToConnection(new List<CkTypeDto>(), arg);
            }

            var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();
            var resultSet =
                await tenantRepository.GetCkTypeAsync(sessionAccessor.Session, modelIdList,
                    keysList, dataQueryOperation, offset, arg.First);

            _logger.LogDebug("GraphQL query handling returning data for construction kit entities");
            return ConnectionUtils.ToConnection(resultSet.Items.Select(CkTypeDtoType.CreateCkTypeDto), arg,
                resultSet.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSet.TotalCount,
                resultSet.AggregationResult, resultSet.FieldAggregationResult);
        }
        catch (Exception e)
        {
            return arg.HandleException(e);
        }
    }

    private async Task<object?> ResolveCkAttributesQuery(IResolveConnectionContext<object?> arg)
    {
        try
        {
            _logger.LogDebug("GraphQL query handling of construction kit attributes started");

            var sessionAccessor = arg.GetSessionAccessor();
            var graphQlUserContext = (GraphQlUserContext)arg.UserContext;

            var offset = arg.GetOffset();
            var dataQueryOperation = arg.GetDataQueryOperation();

            var modelIdList = new List<CkModelId>();
            if (arg.TryGetArgument(Statics.CkModelIds, null, out IEnumerable<string>? modelIds))
            {
                modelIdList.AddRange(modelIds.Select(k => new CkModelId(k)));
            }

            var keysList = new List<CkId<CkAttributeId>>();
            if (arg.TryGetArgument(Statics.CkIdArg, out string? key))
            {
                keysList.Add(new CkId<CkAttributeId>(key));
            }

            if (arg.TryGetArgument(Statics.CkIdsArg, null, out IEnumerable<string>? keys))
            {
                keysList.AddRange(keys.Select(k => new CkId<CkAttributeId>(k)));
            }

            // If argument defined, but empty array, do not return any data. That must be a mistake by client (otherwise
            // all entities are returned)
            if (!keysList.Any() && (arg.HasArgument(Statics.CkIdArg) || arg.HasArgument(Statics.CkIdsArg)))
            {
                return ConnectionUtils.ToConnection(new List<RtEntityDto>(), arg);
            }

            var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();
            var resultSet =
                await tenantRepository.GetCkAttributesAsync(sessionAccessor.Session, modelIdList,
                    keysList, dataQueryOperation, offset, arg.First);

            _logger.LogDebug("GraphQL query handling returning data for construction kit attributes");
            return ConnectionUtils.ToConnection(resultSet.Items.Select(CkAttributeDtoType.CreateCkAttributeDto), arg,
                resultSet.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSet.TotalCount);
        }
        catch (Exception e)
        {
            return arg.HandleException(e);
        }
    }
}