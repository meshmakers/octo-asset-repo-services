using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GraphQL;
using GraphQL.Builders;
using GraphQL.DataLoader;
using GraphQL.Types;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration.DependencyInjection.Options;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;
using Meshmakers.Octo.SystematizedData.Persistence;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
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


    internal OctoQuery(IOptions<OctoAssetRepositoryServicesOptions> options, IGraphTypesCache entityDtoCache,
        IDataLoaderContextAccessor dataLoaderContextAccessor,
        IOctoSessionAccessor octoSessionAccessor, IEnumerable<RtEntityDtoType> rtEntityDtoTypes)
    {
        _options = options;
        _dataLoaderContextAccessor = dataLoaderContextAccessor;
        _octoSessionAccessor = octoSessionAccessor;
        Name = "OctoQuery";

        Connection<CkEntityDtoType>()
            .Argument<StringGraphType>(Statics.CkIdArg, "Returns the construction kit type with the given id.")
            .Argument<ListGraphType<StringGraphType>>(Statics.CkIdsArg,
                "Returns the construction kit types with the given ids.")
            .Argument<SearchFilterDtoType>(Statics.SearchFilterArg, "Filters items based on text search")
            .Argument<ListGraphType<SortDtoType>>(Statics.SortOrderArg, "Sort order for items")
            .Argument<ListGraphType<FieldFilterDtoType>>(Statics.FieldFilterArg,
                "Filters items based on field compare")
            .Name("ConstructionKitTypes")
            .ResolveAsync(ResolveCkEntitiesQuery);

        Connection<CkAttributeDtoType>()
            .Argument<StringGraphType>(Statics.AttributeIdArg, "Returns the entity with the given attribute id.")
            .Argument<ListGraphType<StringGraphType>>(Statics.AttributeIdsArg,
                "Returns entities with the given attribute ids.")
            .Argument<SearchFilterDtoType>(Statics.SearchFilterArg, "Filters items based on text search")
            .Argument<ListGraphType<SortDtoType>>(Statics.SortOrderArg, "Sort order for items")
            .Argument<ListGraphType<FieldFilterDtoType>>(Statics.FieldFilterArg,
                "Filters items based on field compare")
            .Name("ConstructionKitAttributes")
            .ResolveAsync(ResolveCkAttributesQuery);

        Connection<RtEntityGenericDtoType>()
            .Argument<StringGraphType>(Statics.CkIdArg, "The construction kit type with the given id.")
            .Argument<OctoObjectIdType>(Statics.RtIdArg, "Returns the entity with the given rtId.")
            .Argument<ListGraphType<OctoObjectIdType>>(Statics.RtIdsArg,
                "Returns entities with the given rtIds.")
            .Argument<SearchFilterDtoType>(Statics.SearchFilterArg, "Filters items based on text search")
            .Argument<ListGraphType<SortDtoType>>(Statics.SortOrderArg, "Sort order for items")
            .Argument<ListGraphType<FieldFilterDtoType>>(Statics.FieldFilterArg,
                "Filters items based on field compare")
            .Name("RuntimeEntities")
            .ResolveAsync(ResolveGenericRtEntitiesQuery);

        Connection<LargeBinaryInfoDtoType>()
            .Argument<OctoObjectIdType>(Statics.LargeBinaryIdArg, "ID of large binary that is requested.")
            .Name("sysLargeBinaries")
            .ResolveAsync(ResolveLargeBinariesQuery);

        foreach (var rtEntityDtoType in rtEntityDtoTypes)
        {
            this.Connection<object, IGraphType, RtEntityDto>(entityDtoCache, rtEntityDtoType, rtEntityDtoType.Name)
                .AddMetadata(Statics.CkId, rtEntityDtoType.CkId)
                .Argument<OctoObjectIdType>(Statics.RtIdArg, "Returns the entity with the given rtId.")
                .Argument<ListGraphType<OctoObjectIdType>>(Statics.RtIdsArg,
                    "Returns entities with the given rtIds.")
                .Argument<SearchFilterDtoType>(Statics.SearchFilterArg, "Filters items based on text search")
                .Argument<ListGraphType<SortDtoType>>(Statics.SortOrderArg, "Sort order for items")
                .Argument<ListGraphType<FieldFilterDtoType>>(Statics.FieldFilterArg,
                    "Filters items based on field compare")
                .ResolveAsync(ResolveRtEntitiesQuery);
        }
    }

    private async Task<object?> ResolveCkAttributesQuery(IResolveConnectionContext<object?> arg)
    {
        Logger.Debug("GraphQL query handling of contruction kit attributes started");

        var graphQlUserContext = (GraphQLUserContext)arg.UserContext;

        var offset = arg.GetOffset();
        var dataQueryOperation = arg.GetDataQueryOperation();

        arg.TryGetArgument(Statics.AttributeIdArg, out var _, out string key);
        arg.TryGetArgument(Statics.AttributeIdsArg, null, out var hasKeysDefined, out IEnumerable<string> keys);
        var keysList = keys?.ToList();
        // if argument defined, but empty array, do not return any data. That mus be a mistake by client (otherwise
        // all entities are returned.
        if (hasKeysDefined && keysList != null && !keysList.Any())
        {
            return ConnectionUtils.ToConnection(new List<CkAttributeDto>(), arg);
        }

        if ((keysList == null || !keysList.Any()) && key != null)
        {
            keysList = new List<string> { key };
        }

        var resultSet =
            await graphQlUserContext.TenantContext.Repository.GetCkAttributesAsync(_octoSessionAccessor.Session,
                keysList, dataQueryOperation, offset, arg.First);

        Logger.Debug("GraphQL query handling returning data for contruction kit attributes");
        return ConnectionUtils.ToConnection(resultSet.Result.Select(CkAttributeDtoType.CreateCkAttributeDto), arg,
            resultSet.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSet.TotalCount);
    }

    private async Task<object?> ResolveLargeBinariesQuery(IResolveConnectionContext<object?> context)
    {
        Logger.Debug("GraphQL query handling of large binaries started");

        context.TryGetArgument(Statics.LargeBinaryIdArg, out var _, out OctoObjectId key);


        var tenantContext = Helpers.GetTenantContext(context.UserContext);

        var downloadInfo = await tenantContext.Repository.GetLargeBinaryAsync(key);

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
            0, 1);
    }

    private async Task<object?> ResolveCkEntitiesQuery(IResolveConnectionContext<object?> arg)
    {
        Logger.Debug("GraphQL query handling of contruction kit entities started");

        var graphQlUserContext = (GraphQLUserContext)arg.UserContext;

        var offset = arg.GetOffset();
        var dataQueryOperation = arg.GetDataQueryOperation();

        arg.TryGetArgument(Statics.CkIdArg, out var _, out string key);
        arg.TryGetArgument(Statics.CkIdsArg, null, out var hasKeysDefined, out IEnumerable<string> keys);
        var keysList = keys?.ToList();
        // if argument defined, but empty array, do not return any data. That mus be a mistake by client (otherwise
        // all entities are returned.
        if (hasKeysDefined && keysList != null && !keysList.Any())
        {
            return ConnectionUtils.ToConnection(new List<CkEntityDto>(), arg);
        }

        if ((keysList == null || !keysList.Any()) && key != null)
        {
            keysList = new List<string> { key };
        }

        var resultSet =
            await graphQlUserContext.TenantContext.Repository.GetCkEntityAsync(_octoSessionAccessor.Session,
                keysList, dataQueryOperation, offset, arg.First);

        Logger.Debug("GraphQL query handling returning data for contruction kit entities");
        return ConnectionUtils.ToConnection(resultSet.Result.Select(CkEntityDtoType.CreateCkEntityDto), arg,
            resultSet.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSet.TotalCount);
    }

    private async Task<object?> ResolveGenericRtEntitiesQuery(IResolveConnectionContext<object?> arg)
    {
        Logger.Debug("GraphQL query handling for generic runtime entity started");

        var graphQlUserContext = (GraphQLUserContext)arg.UserContext;
        var ckId = arg.GetArgument<string>(Statics.CkIdArg);

        var offset = arg.GetOffset();
        var dataQueryOperation = arg.GetDataQueryOperation();

        arg.TryGetArgument(Statics.RtIdArg, out var _, out OctoObjectId? key);
        arg.TryGetArgument(Statics.RtIdsArg, null, out var hasKeysDefined, out IEnumerable<ObjectId> keys);
        var keysList = keys?.ToList();

        // if argument defined, but empty array, do not return any data. That mus be a mistake by client (otherwise
        // all entities are returned.
        if (hasKeysDefined && keysList != null && !keysList.Any())
        {
            return ConnectionUtils.ToConnection(new List<RtEntityDto>(), arg);
        }

        if (keysList != null && keysList.Any())
        {
            var resultSetIds =
                await graphQlUserContext.TenantContext.Repository.GetRtEntitiesByIdAsync(
                    _octoSessionAccessor.Session, ckId, keysList, dataQueryOperation,
                    offset, arg.First);

            Logger.Debug("GraphQL query handling returning data by keys");
            return ConnectionUtils.ToConnection(resultSetIds.Result.Select(RtEntityDtoType.CreateRtEntityDto), arg,
                resultSetIds.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSetIds.TotalCount);
        }

        if (key.HasValue)
        {
            var result =
                await graphQlUserContext.TenantContext.Repository.GetRtEntityAsync(_octoSessionAccessor.Session,
                    new RtEntityId(ckId, key.Value));

            var resultList = new List<RtEntityDto>();
            if (result != null)
            {
                resultList.Add(RtEntityDtoType.CreateRtEntityDto(result));
            }

            Logger.Debug("GraphQL query handling returning data by key");
            return ConnectionUtils.ToConnection(resultList, arg);
        }

        var resultSet =
            await graphQlUserContext.TenantContext.Repository.GetRtEntitiesByTypeAsync(_octoSessionAccessor.Session,
                ckId, dataQueryOperation, offset,
                arg.First);

        Logger.Debug("GraphQL query handling returning data");
        return ConnectionUtils.ToConnection(resultSet.Result.Select(RtEntityDtoType.CreateRtEntityDto), arg,
            resultSet.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSet.TotalCount);
    }

    private async Task<object?> ResolveRtEntitiesQuery(IResolveConnectionContext<RtEntityDto?> arg)
    {
        Logger.Debug("GraphQL query handling for specific runtime entity type started");

        var graphQlUserContext = (GraphQLUserContext)arg.UserContext;
        var ckId = (string)arg.FieldDefinition.Metadata[Statics.CkId];

        var offset = arg.GetOffset();
        var dataQueryOperation = arg.GetDataQueryOperation();

        arg.TryGetArgument(Statics.RtIdArg, out var _, out OctoObjectId? key);
        arg.TryGetArgument(Statics.RtIdsArg, null, out var hasKeysDefined, out IEnumerable<OctoObjectId> keys);
        var keysList = keys?.Select(x => x.ToObjectId()).ToList();

        // if argument defined, but empty array, do not return any data. That mus be a mistake by client (otherwise
        // all entities are returned.
        if (hasKeysDefined && keysList != null && !keysList.Any())
        {
            return ConnectionUtils.ToConnection(new List<RtEntityDto>(), arg);
        }

        if (keysList != null && keysList.Any())
        {
            var resultSetIds =
                await graphQlUserContext.TenantContext.Repository.GetRtEntitiesByIdAsync(
                    _octoSessionAccessor.Session, ckId, keysList, dataQueryOperation,
                    offset, arg.First);

            Logger.Debug("GraphQL query handling returning data by keys");
            return ConnectionUtils.ToConnection(resultSetIds.Result.Select(RtEntityDtoType.CreateRtEntityDto), arg,
                resultSetIds.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSetIds.TotalCount);
        }

        if (key.HasValue)
        {
            var result =
                await graphQlUserContext.TenantContext.Repository.GetRtEntityAsync(_octoSessionAccessor.Session,
                    new RtEntityId(ckId, key.Value));

            var resultList = new List<RtEntityDto>();
            if (result != null)
            {
                resultList.Add(RtEntityDtoType.CreateRtEntityDto(result));
            }

            Logger.Debug("GraphQL query handling returning data by key");
            return ConnectionUtils.ToConnection(resultList, arg);
        }

        var resultSet =
            await graphQlUserContext.TenantContext.Repository.GetRtEntitiesByTypeAsync(_octoSessionAccessor.Session,
                ckId, dataQueryOperation, offset,
                arg.First);

        Logger.Debug("GraphQL query handling returning data");
        return ConnectionUtils.ToConnection(resultSet.Result.Select(RtEntityDtoType.CreateRtEntityDto), arg,
            resultSet.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0, (int)resultSet.TotalCount);
    }
}
