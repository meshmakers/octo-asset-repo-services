using GraphQL;
using GraphQL.Builders;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;

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
            .Argument<StringGraphType>(Statics.RtCkIdArg,
                "Returns the construction kit type with the given runtime construction kit id.")
            .Argument<ListGraphType<StringGraphType>>(Statics.RtCkIdsArg,
                "Returns the construction kit types with the given runtime construction kit ids.")
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
            .Argument<StringGraphType>(Statics.RtCkIdArg,
                "Returns the construction kit attribute with the given runtime construction kit id.")
            .Argument<ListGraphType<StringGraphType>>(Statics.RtCkIdsArg,
                "Returns the construction kit attributes with the given runtime construction kit ids.")
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
            .Argument<StringGraphType>(Statics.RtCkIdArg,
                "Returns the construction kit enum with the given runtime construction kit id.")
            .Argument<ListGraphType<StringGraphType>>(Statics.RtCkIdsArg,
                "Returns the construction kit enums with the given runtime construction kit ids.")
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
            .Argument<StringGraphType>(Statics.RtCkIdArg,
                "Returns the construction kit record with the given runtime construction kit id.")
            .Argument<ListGraphType<StringGraphType>>(Statics.RtCkIdsArg,
                "Returns the construction kit records with the given runtime construction kit ids.")
            .Argument<SearchFilterDtoType>(Statics.SearchFilterArg, "Filters items based on text search")
            .Argument<ListGraphType<SortDtoType>>(Statics.SortOrderArg, "Sort order for items")
            .Argument<ListGraphType<FieldFilterDtoType>>(Statics.FieldFilterArg,
                "Filters items based on field compare")
            .ResolveAsync(ResolveCkRecordQuery);

        Connection<CkAssociationRoleDtoType>("AssociationRoles")
            .Argument<ListGraphType<StringGraphType>>(Statics.CkModelIds, "Filters items based on model ids")
            .Argument<StringGraphType>(Statics.CkIdArg,
                "Returns the association role with the given association role id.")
            .Argument<ListGraphType<StringGraphType>>(Statics.CkIdsArg,
                "Returns association roles with the given association role ids.")
            .Argument<StringGraphType>(Statics.RtCkIdArg,
                "Returns the construction kit record with the given runtime construction kit id.")
            .Argument<ListGraphType<StringGraphType>>(Statics.RtCkIdsArg,
                "Returns the construction kit association roles with the given runtime construction kit ids.")
            .Argument<SearchFilterDtoType>(Statics.SearchFilterArg, "Filters items based on text search")
            .Argument<ListGraphType<SortDtoType>>(Statics.SortOrderArg, "Sort order for items")
            .Argument<ListGraphType<FieldFilterDtoType>>(Statics.FieldFilterArg,
                "Filters items based on field compare")
            .ResolveAsync(ResolveCkAssociationRoleQuery);
    }

    private async Task<object?> ResolveCkModelsQuery(IResolveConnectionContext<object?> arg)
    {
        try
        {
            _logger.LogDebug("GraphQL query handling of construction kit models started");

            var sessionAccessor = arg.GetSessionAccessor();

            var graphQlUserContext = (GraphQlUserContext)arg.UserContext;

            var offset = arg.GetOffset();
            var queryOptions = arg.GetQueryOptions();

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
                return ConnectionUtils.ToOctoConnection(new List<CkModelDto>(), arg);
            }

            var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();
            var resultSet =
                await tenantRepository.GetCkModelsAsync(sessionAccessor.Session,
                    keysList, queryOptions, offset, arg.First);

            _logger.LogDebug("GraphQL query handling returning data for construction kit models");
            return ConnectionUtils.ToOctoConnection(resultSet.Items.Select(CkModelDtoType.CreateCkModelDto), arg,
                resultSet.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSet.TotalCount,
                resultSet.AggregationResult, resultSet.FieldAggregationResult);
        }
        catch (Exception e)
        {
            return arg.HandleException(e);
        }
    }

    private async Task<object?> ResolveCkAssociationRoleQuery(IResolveConnectionContext<object?> arg)
    {
        try
        {
            _logger.LogDebug("GraphQL query handling of construction kit association role started");

            var sessionAccessor = arg.GetSessionAccessor();

            var graphQlUserContext = (GraphQlUserContext)arg.UserContext;

            var offset = arg.GetOffset();
            var queryOptions = arg.GetQueryOptions();

            var modelIdList = new List<CkModelId>();
            if (arg.TryGetArgument(Statics.CkModelIds, null, out IEnumerable<string>? modelIds))
            {
                modelIdList.AddRange(modelIds.Select(k => new CkModelId(k)));
            }

            var ckIdsList = new List<CkId<CkAssociationRoleId>>();
            if (arg.TryGetArgument(Statics.CkIdArg, out string? ckId))
            {
                ckIdsList.Add(new CkId<CkAssociationRoleId>(ckId));
            }

            if (arg.TryGetArgument(Statics.CkIdsArg, null, out IEnumerable<string>? ckIds))
            {
                ckIdsList.AddRange(ckIds.Select(k => new CkId<CkAssociationRoleId>(k)));
            }

            var rtCkIdsList = new List<RtCkId<CkAssociationRoleId>>();
            if (arg.TryGetArgument(Statics.RtCkIdArg, out string? rtCkId))
            {
                rtCkIdsList.Add(new RtCkId<CkAssociationRoleId>(rtCkId));
            }

            if (arg.TryGetArgument(Statics.RtCkIdsArg, null, out IEnumerable<string>? rtCkIds))
            {
                rtCkIdsList.AddRange(rtCkIds.Select(k => new RtCkId<CkAssociationRoleId>(k)));
            }

            // If argument defined, but empty array, do not return any data. That must be a mistake by client (otherwise
            // all entities are returned)
            if (!ckIdsList.Any() && !rtCkIdsList.Any() &&
                (arg.HasArgument(Statics.CkIdArg) || arg.HasArgument(Statics.CkIdsArg) ||
                 arg.HasArgument(Statics.RtCkIdArg) || arg.HasArgument(Statics.RtCkIdsArg)))
            {
                return ConnectionUtils.ToOctoConnection(new List<CkAssociationRoleDto>(), arg);
            }

            if ((ckIdsList.Any() || rtCkIdsList.Any()) && modelIdList.Any())
            {
                throw AssetRepositoryException.InvalidArgumentsCkIdOrRtCkIdAndModelIdInSameQuery();
            }

            if (ckIdsList.Any() && rtCkIdsList.Any())
            {
                throw AssetRepositoryException.InvalidArgumentsCkIdAndRtCkIdInSameQuery();
            }

            var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();
            IResultSet<CkAssociationRole>? resultSet;
            if (rtCkIdsList.Any())
            {
                resultSet = await tenantRepository.GetCkAssociationRoleAsync(sessionAccessor.Session,
                    rtCkIdsList, queryOptions, offset, arg.First);
            }
            else if (ckIdsList.Any())
            {
                resultSet = await tenantRepository.GetCkAssociationRoleAsync(sessionAccessor.Session,
                    ckIdsList, queryOptions, offset, arg.First);
            }
            else
            {
                resultSet = await tenantRepository.GetCkAssociationRoleAsync(sessionAccessor.Session, modelIdList,
                    queryOptions, offset, arg.First);
            }

            _logger.LogDebug("GraphQL query handling returning data for construction kit association roles");
            return ConnectionUtils.ToOctoConnection(resultSet.Items.Select(CkAssociationRoleDtoType.CreateCkAssociationRoleDto), arg,
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
            var queryOptions = arg.GetQueryOptions();

            var modelIdList = new List<CkModelId>();
            if (arg.TryGetArgument(Statics.CkModelIds, null, out IEnumerable<string>? modelIds))
            {
                modelIdList.AddRange(modelIds.Select(k => new CkModelId(k)));
            }

            var ckIdsList = new List<CkId<CkRecordId>>();
            if (arg.TryGetArgument(Statics.CkIdArg, out string? ckId))
            {
                ckIdsList.Add(new CkId<CkRecordId>(ckId));
            }

            if (arg.TryGetArgument(Statics.CkIdsArg, null, out IEnumerable<string>? ckIds))
            {
                ckIdsList.AddRange(ckIds.Select(k => new CkId<CkRecordId>(k)));
            }

            var rtCkIdsList = new List<RtCkId<CkRecordId>>();
            if (arg.TryGetArgument(Statics.RtCkIdArg, out string? rtCkId))
            {
                rtCkIdsList.Add(new RtCkId<CkRecordId>(rtCkId));
            }

            if (arg.TryGetArgument(Statics.RtCkIdsArg, null, out IEnumerable<string>? rtCkIds))
            {
                rtCkIdsList.AddRange(rtCkIds.Select(k => new RtCkId<CkRecordId>(k)));
            }

            // If argument defined, but empty array, do not return any data. That must be a mistake by client (otherwise
            // all entities are returned)
            if (!ckIdsList.Any() && !rtCkIdsList.Any() &&
                (arg.HasArgument(Statics.CkIdArg) || arg.HasArgument(Statics.CkIdsArg) ||
                 arg.HasArgument(Statics.RtCkIdArg) || arg.HasArgument(Statics.RtCkIdsArg)))
            {
                return ConnectionUtils.ToOctoConnection(new List<CkRecordDto>(), arg);
            }

            if ((ckIdsList.Any() || rtCkIdsList.Any()) && modelIdList.Any())
            {
                throw AssetRepositoryException.InvalidArgumentsCkIdOrRtCkIdAndModelIdInSameQuery();
            }

            if (ckIdsList.Any() && rtCkIdsList.Any())
            {
                throw AssetRepositoryException.InvalidArgumentsCkIdAndRtCkIdInSameQuery();
            }

            var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();
            IResultSet<CkRecord>? resultSet;
            if (rtCkIdsList.Any())
            {
                resultSet = await tenantRepository.GetCkRecordAsync(sessionAccessor.Session,
                    rtCkIdsList, queryOptions, offset, arg.First);
            }
            else if (ckIdsList.Any())
            {
                resultSet = await tenantRepository.GetCkRecordAsync(sessionAccessor.Session,
                    ckIdsList, queryOptions, offset, arg.First);
            }
            else
            {
                resultSet = await tenantRepository.GetCkRecordAsync(sessionAccessor.Session, modelIdList,
                    queryOptions, offset, arg.First);
            }

            _logger.LogDebug("GraphQL query handling returning data for construction kit records");
            return ConnectionUtils.ToOctoConnection(resultSet.Items.Select(CkRecordDtoType.CreateCkRecordDto), arg,
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
            var queryOptions = arg.GetQueryOptions();

            var modelIdList = new List<CkModelId>();
            if (arg.TryGetArgument(Statics.CkModelIds, null, out IEnumerable<string>? modelIds))
            {
                modelIdList.AddRange(modelIds.Select(k => new CkModelId(k)));
            }

            var ckIdsList = new List<CkId<CkEnumId>>();
            if (arg.TryGetArgument(Statics.CkIdArg, out string? ckId))
            {
                ckIdsList.Add(new CkId<CkEnumId>(ckId));
            }

            if (arg.TryGetArgument(Statics.CkIdsArg, null, out IEnumerable<string>? ckIds))
            {
                ckIdsList.AddRange(ckIds.Select(k => new CkId<CkEnumId>(k)));
            }

            var rtCkIdsList = new List<RtCkId<CkEnumId>>();
            if (arg.TryGetArgument(Statics.RtCkIdArg, out string? rtCkId))
            {
                rtCkIdsList.Add(new RtCkId<CkEnumId>(rtCkId));
            }

            if (arg.TryGetArgument(Statics.RtCkIdsArg, null, out IEnumerable<string>? rtCkIds))
            {
                rtCkIdsList.AddRange(rtCkIds.Select(k => new RtCkId<CkEnumId>(k)));
            }

            // If argument defined, but empty array, do not return any data. That must be a mistake by client (otherwise
            // all entities are returned)
            if (!ckIdsList.Any() && !rtCkIdsList.Any() &&
                (arg.HasArgument(Statics.CkIdArg) || arg.HasArgument(Statics.CkIdsArg) ||
                 arg.HasArgument(Statics.RtCkIdArg) || arg.HasArgument(Statics.RtCkIdsArg)))
            {
                return ConnectionUtils.ToOctoConnection(new List<CkEnumDto>(), arg);
            }

            if ((ckIdsList.Any() || rtCkIdsList.Any()) && modelIdList.Any())
            {
                throw AssetRepositoryException.InvalidArgumentsCkIdOrRtCkIdAndModelIdInSameQuery();
            }

            if (ckIdsList.Any() && rtCkIdsList.Any())
            {
                throw AssetRepositoryException.InvalidArgumentsCkIdAndRtCkIdInSameQuery();
            }

            var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();
            IResultSet<CkEnum>? resultSet;
            if (rtCkIdsList.Any())
            {
                resultSet = await tenantRepository.GetCkEnumAsync(sessionAccessor.Session,
                    rtCkIdsList, queryOptions, offset, arg.First);
            }
            else if (ckIdsList.Any())
            {
                resultSet = await tenantRepository.GetCkEnumAsync(sessionAccessor.Session,
                    ckIdsList, queryOptions, offset, arg.First);
            }
            else
            {
                resultSet = await tenantRepository.GetCkEnumAsync(sessionAccessor.Session, modelIdList,
                    queryOptions, offset, arg.First);
            }

            _logger.LogDebug("GraphQL query handling returning data for construction kit enums");
            return ConnectionUtils.ToOctoConnection(resultSet.Items.Select(CkEnumDtoType.CreateCkEnumDto), arg,
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
            var queryOptions = arg.GetQueryOptions();

            var modelIdList = new List<CkModelId>();
            if (arg.TryGetArgument(Statics.CkModelIds, null, out IEnumerable<string>? modelIds))
            {
                modelIdList.AddRange(modelIds.Select(k => new CkModelId(k)));
            }

            var ckIdsList = new List<CkId<CkTypeId>>();
            if (arg.TryGetArgument(Statics.CkIdArg, out string? ckTypeId))
            {
                ckIdsList.Add(new CkId<CkTypeId>(ckTypeId));
            }

            if (arg.TryGetArgument(Statics.CkIdsArg, null, out IEnumerable<string>? ckTypeIds))
            {
                ckIdsList.AddRange(ckTypeIds.Select(k => new CkId<CkTypeId>(k)));
            }

            var rtCkIdsList = new List<RtCkId<CkTypeId>>();
            if (arg.TryGetArgument(Statics.RtCkIdArg, out string? rtCkId))
            {
                rtCkIdsList.Add(new RtCkId<CkTypeId>(rtCkId));
            }

            if (arg.TryGetArgument(Statics.RtCkIdsArg, null, out IEnumerable<string>? rtCkIds))
            {
                rtCkIdsList.AddRange(rtCkIds.Select(k => new RtCkId<CkTypeId>(k)));
            }

            // If argument defined, but empty array, do not return any data. That must be a mistake by client (otherwise
            // all entities are returned)
            if (!ckIdsList.Any() && !rtCkIdsList.Any() &&
                (arg.HasArgument(Statics.CkIdArg) || arg.HasArgument(Statics.CkIdsArg) ||
                 arg.HasArgument(Statics.RtCkIdArg) || arg.HasArgument(Statics.RtCkIdsArg)))
            {
                return ConnectionUtils.ToOctoConnection(new List<CkTypeDto>(), arg);
            }

            if ((ckIdsList.Any() || rtCkIdsList.Any()) && modelIdList.Any())
            {
                throw AssetRepositoryException.InvalidArgumentsCkIdOrRtCkIdAndModelIdInSameQuery();
            }

            if (ckIdsList.Any() && rtCkIdsList.Any())
            {
                throw AssetRepositoryException.InvalidArgumentsCkIdAndRtCkIdInSameQuery();
            }

            var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();
            IResultSet<CkType>? resultSet;
            if (rtCkIdsList.Any())
            {
                resultSet = await tenantRepository.GetCkTypeAsync(sessionAccessor.Session,
                    rtCkIdsList, queryOptions, offset, arg.First);
            }
            else if (ckIdsList.Any())
            {
                resultSet = await tenantRepository.GetCkTypeAsync(sessionAccessor.Session,
                    ckIdsList, queryOptions, offset, arg.First);
            }
            else
            {
                resultSet = await tenantRepository.GetCkTypeAsync(sessionAccessor.Session, modelIdList,
                    queryOptions, offset, arg.First);
            }

            _logger.LogDebug("GraphQL query handling returning data for construction kit entities");
            return ConnectionUtils.ToOctoConnection(resultSet.Items.Select(CkTypeDtoType.CreateCkTypeDto), arg,
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
            var queryOptions = arg.GetQueryOptions();

            var modelIdList = new List<CkModelId>();
            if (arg.TryGetArgument(Statics.CkModelIds, null, out IEnumerable<string>? modelIds))
            {
                modelIdList.AddRange(modelIds.Select(k => new CkModelId(k)));
            }

            var ckIdsList = new List<CkId<CkAttributeId>>();
            if (arg.TryGetArgument(Statics.CkIdArg, out string? ckId))
            {
                ckIdsList.Add(new CkId<CkAttributeId>(ckId));
            }

            if (arg.TryGetArgument(Statics.CkIdsArg, null, out IEnumerable<string>? ckIds))
            {
                ckIdsList.AddRange(ckIds.Select(k => new CkId<CkAttributeId>(k)));
            }

            var rtCkIdsList = new List<RtCkId<CkAttributeId>>();
            if (arg.TryGetArgument(Statics.RtCkIdArg, out string? rtCkId))
            {
                rtCkIdsList.Add(new RtCkId<CkAttributeId>(rtCkId));
            }

            if (arg.TryGetArgument(Statics.RtCkIdsArg, null, out IEnumerable<string>? rtCkIds))
            {
                rtCkIdsList.AddRange(rtCkIds.Select(k => new RtCkId<CkAttributeId>(k)));
            }

            // If argument defined, but empty array, do not return any data. That must be a mistake by client (otherwise
            // all entities are returned)
            if (!ckIdsList.Any() && !rtCkIdsList.Any() &&
                (arg.HasArgument(Statics.CkIdArg) || arg.HasArgument(Statics.CkIdsArg) ||
                 arg.HasArgument(Statics.RtCkIdArg) || arg.HasArgument(Statics.RtCkIdsArg)))
            {
                return ConnectionUtils.ToOctoConnection(new List<CkAttributeDto>(), arg);
            }

            if ((ckIdsList.Any() || rtCkIdsList.Any()) && modelIdList.Any())
            {
                throw AssetRepositoryException.InvalidArgumentsCkIdOrRtCkIdAndModelIdInSameQuery();
            }

            if (ckIdsList.Any() && rtCkIdsList.Any())
            {
                throw AssetRepositoryException.InvalidArgumentsCkIdAndRtCkIdInSameQuery();
            }

            var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();
            IResultSet<CkAttribute>? resultSet;
            if (rtCkIdsList.Any())
            {
                resultSet = await tenantRepository.GetCkAttributesAsync(sessionAccessor.Session,
                    rtCkIdsList, queryOptions, offset, arg.First);
            }
            else if (ckIdsList.Any())
            {
                resultSet = await tenantRepository.GetCkAttributesAsync(sessionAccessor.Session,
                    ckIdsList, queryOptions, offset, arg.First);
            }
            else
            {
                resultSet = await tenantRepository.GetCkAttributesAsync(sessionAccessor.Session, modelIdList,
                    queryOptions, offset, arg.First);
            }

            _logger.LogDebug("GraphQL query handling returning data for construction kit attributes");
            return ConnectionUtils.ToOctoConnection(resultSet.Items.Select(CkAttributeDtoType.CreateCkAttributeDto), arg,
                resultSet.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSet.TotalCount);
        }
        catch (Exception e)
        {
            return arg.HandleException(e);
        }
    }
}