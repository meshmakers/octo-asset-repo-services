using GraphQL;
using GraphQL.Builders;
using GraphQL.DataLoader;
using GraphQL.Types;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration.DependencyInjection.Options;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Microsoft.Extensions.Options;
using NLog;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

/// <summary>
///     Implements an Octo query, based on a given data source
/// </summary>
internal sealed class OctoQuery : ObjectGraphType
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly IDataLoaderContextAccessor _dataLoaderContextAccessor;
    private readonly IOctoSessionAccessor _octoSessionAccessor;
    private readonly IOptions<OctoAssetRepositoryServicesOptions> _options;


    internal OctoQuery(IOptions<OctoAssetRepositoryServicesOptions> options, IGraphTypesCache graphTypesCache,
        IDataLoaderContextAccessor dataLoaderContextAccessor,
        IOctoSessionAccessor octoSessionAccessor)
    {
        _options = options;
        _dataLoaderContextAccessor = dataLoaderContextAccessor;
        _octoSessionAccessor = octoSessionAccessor;
        Name = "OctoQuery";

        Connection<CkTypeDtoType>("ConstructionKitTypes")
            .Argument<StringGraphType>(Statics.CkIdArg, "Returns the construction kit type with the given id.")
            .Argument<ListGraphType<StringGraphType>>(Statics.CkIdsArg,
                "Returns the construction kit types with the given ids.")
            .Argument<SearchFilterDtoType>(Statics.SearchFilterArg, "Filters items based on text search")
            .Argument<ListGraphType<SortDtoType>>(Statics.SortOrderArg, "Sort order for items")
            .Argument<ListGraphType<FieldFilterDtoType>>(Statics.FieldFilterArg,
                "Filters items based on field compare")
            .ResolveAsync(ResolveCkEntitiesQuery);

        Connection<CkAttributeDtoType>("ConstructionKitAttributes")
            .Argument<StringGraphType>(Statics.AttributeIdArg, "Returns the entity with the given attribute id.")
            .Argument<ListGraphType<StringGraphType>>(Statics.AttributeIdsArg,
                "Returns entities with the given attribute ids.")
            .Argument<SearchFilterDtoType>(Statics.SearchFilterArg, "Filters items based on text search")
            .Argument<ListGraphType<SortDtoType>>(Statics.SortOrderArg, "Sort order for items")
            .Argument<ListGraphType<FieldFilterDtoType>>(Statics.FieldFilterArg,
                "Filters items based on field compare")
            .ResolveAsync(ResolveCkAttributesQuery);

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

        Connection<LargeBinaryInfoDtoType>("sysLargeBinaries")
            .Argument<OctoObjectIdType>(Statics.LargeBinaryIdArg, "ID of large binary that is requested.")
            .ResolveAsync(ResolveLargeBinariesQuery);

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

    private async Task<object?> ResolveCkAttributesQuery(IResolveConnectionContext<object?> arg)
    {
        Logger.Debug("GraphQL query handling of contruction kit attributes started");

        var graphQlUserContext = (GraphQlUserContext)arg.UserContext;

        var offset = arg.GetOffset();
        var dataQueryOperation = arg.GetDataQueryOperation();

        var keysList = new List<CkId<CkAttributeId>>();
        if (arg.TryGetArgument(Statics.AttributeIdArg, out string? key))
        {
            keysList.Add(new CkId<CkAttributeId>(key));
        }

        if (arg.TryGetArgument(Statics.AttributeIdsArg, null, out IEnumerable<string>? keys))
        {
            keysList.AddRange(keys.Select(k => new CkId<CkAttributeId>(k)));
        }

        // if argument defined, but empty array, do not return any data. That mus be a mistake by client (otherwise
        // all entities are returned.
        if (!keysList.Any() && (arg.HasArgument(Statics.AttributeIdArg) || arg.HasArgument(Statics.AttributeIdsArg)))
        {
            return ConnectionUtils.ToConnection(new List<RtEntityDto>(), arg, null);
        }

        var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();
        var resultSet =
            await tenantRepository.GetCkAttributesAsync(_octoSessionAccessor.Session,
                keysList, dataQueryOperation, offset, arg.First);

        Logger.Debug("GraphQL query handling returning data for contruction kit attributes");
        return ConnectionUtils.ToConnection(resultSet.Items.Select(CkAttributeDtoType.CreateCkAttributeDto), arg,
            resultSet.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSet.TotalCount, null);
    }

    private async Task<object?> ResolveLargeBinariesQuery(IResolveConnectionContext<object?> context)
    {
        Logger.Debug("GraphQL query handling of large binaries started");

        context.TryGetArgument(Statics.LargeBinaryIdArg, out OctoObjectId key);


        var tenantContext = Helpers.GetTenantContext(context.UserContext);

        var tenantRepository = tenantContext.GetTenantRepository();
        var downloadInfo = await tenantRepository.GetLargeBinaryAsync(key);

        return ConnectionUtils.ToConnection(
            new[]
            {
                new LargeBinaryInfoDto
                {
                    ContentType = downloadInfo.ContentType,
                    BinaryId = downloadInfo.BinaryId,
                    Filename = downloadInfo.Filename,
                    Length = downloadInfo.Length,
                    UploadDateTime = downloadInfo.UploadDateTime,
                    DownloadUri = new Uri(_options.Value.PublicUrl.EnsureEndsWith(
                        $"/system/v1/largeBinaries?tenantId={tenantContext.TenantId}&largeBinaryId={downloadInfo.BinaryId}"))
                }
            }, context,
            0, 1, null);
    }

    private async Task<object?> ResolveCkEntitiesQuery(IResolveConnectionContext<object?> arg)
    {
        Logger.Debug("GraphQL query handling of contruction kit entities started");

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
            await tenantRepository.GetCkTypeAsync(_octoSessionAccessor.Session,
                keysList, dataQueryOperation, offset, arg.First);

        Logger.Debug("GraphQL query handling returning data for contruction kit entities");
        return ConnectionUtils.ToConnection(resultSet.Items.Select(CkTypeDtoType.CreateCkTypeDto), arg,
            resultSet.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSet.TotalCount, resultSet.Grouping);
    }

    private async Task<object?> ResolveGenericRtEntitiesQuery(IResolveConnectionContext<object?> arg)
    {
        Logger.Debug("GraphQL query handling for generic runtime entity started");

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
                    _octoSessionAccessor.Session, ckId, keysList, dataQueryOperation,
                    offset, arg.First);

            Logger.Debug("GraphQL query handling returning data by keys");
            return ConnectionUtils.ToConnection(resultSetIds.Items.Select(RtEntityDtoType.CreateRtEntityDto), arg,
                resultSetIds.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSetIds.TotalCount, resultSetIds.Grouping);
        }

        var resultSet =
            await tenantRepository.GetRtEntitiesByTypeAsync(_octoSessionAccessor.Session,
                ckId, dataQueryOperation, offset,
                arg.First);

        Logger.Debug("GraphQL query handling returning data");
        return ConnectionUtils.ToConnection(resultSet.Items.Select(RtEntityDtoType.CreateRtEntityDto), arg,
            resultSet.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSet.TotalCount, resultSet.Grouping);
    }

    private async Task<object?> ResolveRtEntitiesQuery(IResolveConnectionContext<RtEntityDto?> arg)
    {
        Logger.Debug("GraphQL query handling for specific runtime entity type started");

        var graphQlUserContext = (GraphQlUserContext)arg.UserContext;

        if (!arg.FieldDefinition.Metadata.TryGetValue(Statics.CkId, out var ckIdObj))
        {
            arg.Errors.Add(new ExecutionError("Invalid query. Missing construction kit id.")
                { Code = CommonConstants.GraphQLErrorCommon });
            return null;
        }

        if (ckIdObj is not CkId<CkTypeId> ckTypeId)
        {
            arg.Errors.Add(new ExecutionError("Invalid query. Invalid construction kit id.")
                { Code = CommonConstants.GraphQLErrorCommon });
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
                    _octoSessionAccessor.Session, ckTypeId, keysList, dataQueryOperation,
                    offset, arg.First);

            Logger.Debug("GraphQL query handling returning data by keys");
            return ConnectionUtils.ToConnection(resultSetIds.Items.Select(RtEntityDtoType.CreateRtEntityDto), arg,
                resultSetIds.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSetIds.TotalCount, resultSetIds.Grouping);
        }

        var resultSet =
            await tenantRepository.GetRtEntitiesByTypeAsync(_octoSessionAccessor.Session,
                ckTypeId, dataQueryOperation, offset,
                arg.First);

        Logger.Debug("GraphQL query handling returning data");
        return ConnectionUtils.ToConnection(resultSet.Items.Select(RtEntityDtoType.CreateRtEntityDto), arg,
            resultSet.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSet.TotalCount, resultSet.Grouping);
    }
}