using AssetRepositoryServices.Resources;
using GraphQL;
using GraphQL.Builders;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

internal sealed class CkModelDtoType : ObjectGraphType<CkModelDto>
{
    private readonly ILogger<CkModelDtoType> _logger;

    public CkModelDtoType(ILogger<CkModelDtoType> logger)
    {
        _logger = logger;
        Name = "CkModel";
        Description = "A construction kit model";

        Field(x => x.Id, typeof(NonNullGraphType<ModelIdType>))
            .Description("Construction kit model id, the unique identifier of the model.");
        Field(x => x.Description, true).Description(AssetTexts.Graphql_Model_Description_Description);
        Field(x => x.ModelState, typeof(ModelStateDtoType))
            .Description("Availability of the model within the repository.");

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

        Field<ListGraphType<ModelIdType>>("dependencies")
            .ResolveAsync(ResolveCkModelDependenciesQuery);
    }

    private static bool GetParameter<TKey>(IResolveConnectionContext<CkModelDto> arg,
        out IOctoSessionAccessor sessionAccessor,
        out GraphQlUserContext graphQlUserContext, out int? offset, out RtEntityQueryOptions queryOptions,
        out List<CkId<TKey>> keysList) where TKey : IComparable<TKey>, ICkElementId
    {
        if (arg.RequestServices == null)
        {
            throw AssetRepositoryException.RequestServicesNotAvailable();
        }

        sessionAccessor = arg.GetSessionAccessor();

        graphQlUserContext = (GraphQlUserContext)arg.UserContext;

        offset = arg.GetOffset();
        queryOptions = arg.GetDataQueryOperation();

        keysList = new List<CkId<TKey>>();
        if (arg.TryGetArgument(Statics.CkIdsArg, null, out IEnumerable<string>? keys))
        {
            keysList.AddRange(keys.Select(k => new CkId<TKey>(k)));
        }

        // if argument defined, but empty array, do not return any data. That mus be a mistake by client (otherwise
        // all entities are returned.
        if (!keysList.Any() && (arg.HasArgument(Statics.CkIdArg) || arg.HasArgument(Statics.CkIdsArg)))
        {
            return false;
        }

        return true;
    }

    private async Task<object?> ResolveCkModelDependenciesQuery(IResolveFieldContext<CkModelDto> arg)
    {
        try
        {
            _logger.LogDebug("GraphQL query handling of construction kit model dependencies started");

            if (arg.RequestServices == null)
            {
                throw AssetRepositoryException.RequestServicesNotAvailable();
            }

            var sessionAccessor = arg.GetSessionAccessor();
            var graphQlUserContext = (GraphQlUserContext)arg.UserContext;
            var queryOptions = RtEntityQueryOptions.Create();

            var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();
            var resultSet =
                await tenantRepository.GetCkModelsAsync(sessionAccessor.Session,
                    [arg.Source.Id], queryOptions, 0, 1);

            if (resultSet.Items.Any() && resultSet.Items.First().Dependencies != null)
            {
                return resultSet.Items.First().Dependencies!.ToList();
            }

            return new List<CkModelId>();
        }
        catch (Exception e)
        {
            return arg.HandleException(e);
        }
    }

    private async Task<object?> ResolveCkTypesQuery(IResolveConnectionContext<CkModelDto> arg)
    {
        try
        {
            _logger.LogDebug("GraphQL query handling of construction kit types started");

            if (!GetParameter<CkTypeId>(arg, out var sessionAccessor, out var graphQlUserContext, out var offset,
                    out var dataQueryOperation, out var keysList))
            {
                return ConnectionUtils.ToConnection(new List<CkTypeDto>(), arg);
            }

            if (sessionAccessor.Session == null)
            {
                throw AssetRepositoryException.SessionUnavailable();
            }

            dataQueryOperation.FieldEquals(nameof(CkType.CkModelId), arg.Source.Id);

            var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();
            var resultSet =
                await tenantRepository.GetCkTypeAsync(sessionAccessor.Session, null,
                    keysList, dataQueryOperation, offset, arg.First);

            _logger.LogDebug("GraphQL query handling returning data for construction kit types");
            return ConnectionUtils.ToConnection(resultSet.Items.Select(CkTypeDtoType.CreateCkTypeDto), arg,
                resultSet.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSet.TotalCount,
                resultSet.AggregationResult, resultSet.FieldAggregationResult);
        }
        catch (Exception e)
        {
            return arg.HandleException(e);
        }
    }

    private async Task<object?> ResolveCkAttributesQuery(IResolveConnectionContext<CkModelDto> arg)
    {
        try
        {
            _logger.LogDebug("GraphQL query handling of construction kit attributes started");

            if (!GetParameter<CkAttributeId>(arg, out var sessionAccessor, out var graphQlUserContext, out var offset,
                    out var dataQueryOperation, out var keysList))
            {
                return ConnectionUtils.ToConnection(new List<CkAttributeDto>(), arg);
            }

            if (sessionAccessor.Session == null)
            {
                throw AssetRepositoryException.SessionUnavailable();
            }

            dataQueryOperation.FieldEquals(nameof(CkAttribute.CkModelId), arg.Source.Id);

            var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();
            var resultSet =
                await tenantRepository.GetCkAttributesAsync(sessionAccessor.Session, null,
                    keysList, dataQueryOperation, offset, arg.First);

            _logger.LogDebug("GraphQL query handling returning data for construction kit attribute");
            return ConnectionUtils.ToConnection(resultSet.Items.Select(CkAttributeDtoType.CreateCkAttributeDto), arg,
                resultSet.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSet.TotalCount,
                resultSet.AggregationResult, resultSet.FieldAggregationResult);
        }
        catch (Exception e)
        {
            return arg.HandleException(e);
        }
    }

    private async Task<object?> ResolveCkEnumQuery(IResolveConnectionContext<CkModelDto> arg)
    {
        try
        {
            _logger.LogDebug("GraphQL query handling of construction kit enums started");

            if (!GetParameter<CkEnumId>(arg, out var sessionAccessor, out var graphQlUserContext, out var offset,
                    out var dataQueryOperation, out var keysList))
            {
                return ConnectionUtils.ToConnection(new List<CkEnumDto>(), arg);
            }

            if (sessionAccessor.Session == null)
            {
                throw AssetRepositoryException.SessionUnavailable();
            }

            dataQueryOperation.FieldEquals(nameof(CkAttribute.CkModelId), arg.Source.Id);

            var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();
            var resultSet =
                await tenantRepository.GetCkEnumAsync(sessionAccessor.Session, null,
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

    private async Task<object?> ResolveCkRecordQuery(IResolveConnectionContext<CkModelDto> arg)
    {
        try
        {
            _logger.LogDebug("GraphQL query handling of construction kit records started");

            if (!GetParameter<CkRecordId>(arg, out var sessionAccessor, out var graphQlUserContext, out var offset,
                    out var dataQueryOperation, out var keysList))
            {
                return ConnectionUtils.ToConnection(new List<CkRecordDto>(), arg);
            }

            if (sessionAccessor.Session == null)
            {
                throw AssetRepositoryException.SessionUnavailable();
            }

            dataQueryOperation.FieldEquals(nameof(CkAttribute.CkModelId), arg.Source.Id);

            var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();
            var resultSet =
                await tenantRepository.GetCkRecordAsync(sessionAccessor.Session, null,
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

    public static CkModelDto CreateCkModelDto(CkModel model)
    {
        return new CkModelDto
        {
            ModelState = model.ModelState,
            Description = model.Description,
            Id = model.Id
        };
    }
}