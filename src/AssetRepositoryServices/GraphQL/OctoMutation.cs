using GraphQL;
using GraphQL.Resolvers;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Messages;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

/// <summary>
///     Implements mutations of Octo
/// </summary>
public sealed class OctoMutation : ObjectGraphType
{
    private readonly IOctoSessionAccessor _sessionAccessor;

    internal OctoMutation(IGraphTypesCache graphTypesCache,
        IOctoSessionAccessor sessionAccessor)
    {
        _sessionAccessor = sessionAccessor;
        foreach (var rtEntityDtoType in graphTypesCache.GetTypes())
        {
            if (rtEntityDtoType.IsAbstract)
            {
                continue;
            }
            var inputType = graphTypesCache.GetInputType(rtEntityDtoType.CkTypeId);
            var outputType = graphTypesCache.GetType(rtEntityDtoType.CkTypeId);

            var createArgument = new QueryArgument(new NonNullGraphType(new ListGraphType(inputType)))
                { Name = Statics.EntitiesArg };
            var updateArgument =
                new QueryArgument(
                        new NonNullGraphType(new ListGraphType(new UpdateMutationDtoType<RtEntityDto>(inputType))))
                    { Name = Statics.EntitiesArg };
            var deleteArgument =
                new QueryArgument(new NonNullGraphType(new ListGraphType(new DeleteMutationDtoType(inputType))))
                    { Name = Statics.EntitiesArg };

            this.FieldAsync($"create{outputType.Name}s", $"Creates new entities of type '{outputType.Name}'.",
                    new ListGraphType(outputType),
                    new QueryArguments(createArgument), ResolveCreate)
                .AddMetadata(Statics.CkId, rtEntityDtoType.CkTypeId);

            this.FieldAsync($"update{outputType.Name}s", $"Updates existing entity of type '{outputType.Name}'.",
                    new ListGraphType(outputType),
                    new QueryArguments(updateArgument), ResolveUpdate)
                .AddMetadata(Statics.CkId, rtEntityDtoType.CkTypeId);

            this.FieldAsync($"delete{outputType.Name}s", $"Deletes an entity of type '{outputType.Name}'.",
                    new BooleanGraphType(),
                    new QueryArguments(deleteArgument), ResolveDelete)
                .AddMetadata(Statics.CkId, rtEntityDtoType.CkTypeId);
        }

        AddField(new FieldType
        {
            Name = "sysCreateLargeBinary",
            Description = "Uploads a large binary and stores it. ID of file is returned.",
            Type = typeof(OctoObjectIdType),
            Arguments = new QueryArguments(
                new QueryArgument(new NonNullGraphType(new LargeBinaryDtoType()))
                    { Name = Statics.LargeBinaryDataArg }
            ),
            Resolver = new FuncFieldResolver<object, OctoObjectId>(ResolveCreateLargeBinary)
        });
    }

    private async ValueTask<OctoObjectId> ResolveCreateLargeBinary(IResolveFieldContext<object> context)
    {
        var tenantContext = Helpers.GetTenantContext(context.UserContext);
        var tenantRepository = tenantContext.GetTenantRepository();

        var file = context.GetArgument<IFormFile>(Statics.LargeBinaryDataArg);
        var fileName = file.FileName;
        var contentType = file.ContentType;

        return await tenantRepository.UploadLargeBinaryAsync(fileName, contentType, file.OpenReadStream(),
            CancellationToken.None);
    }

    private async ValueTask<object?> ResolveDelete(IResolveFieldContext<object?> arg)
    {
        var tenantContext = Helpers.GetTenantContext(arg.UserContext);
        var tenantRepository = tenantContext.GetTenantRepository();

        var ckId = arg.FieldDefinition.GetMetadata<string>(Statics.CkId);

        var inputObjects = arg.GetArgument<List<MutationDto<object>>>(Statics.EntitiesArg);

        try
        {
            var entityUpdateInfos = new List<EntityUpdateInfo<RtEntity>>();
            foreach (var mutationDto in inputObjects)
            {
                entityUpdateInfos.Add(EntityUpdateInfo<RtEntity>.CreateDelete(new RtEntityId(ckId, mutationDto.RtId)));
            }

            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(_sessionAccessor.Session, entityUpdateInfos, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                return false;
            }

            return true;
        }
        catch (OperationFailedException e)
        {
            arg.Errors.Add(new ExecutionError(e.Message, e) { Code = CommonConstants.GraphQLErrorDataStore });
            return false;
        }
        catch (Exception e)
        {
            arg.Errors.Add(new ExecutionError("A general error occurred", e)
                { Code = CommonConstants.GraphQLErrorCommon });
            return false;
        }
    }

    private async ValueTask<object?> ResolveUpdate(IResolveFieldContext<object?> arg)
    {
        var ckCacheService = arg.RequestServices?.GetRequiredService<ICkCacheService>();
        if (ckCacheService == null)
        {
            throw AssetRepositoryException.ServiceNotRegistered(typeof(ICkCacheService));
        }

        var tenantContext = Helpers.GetTenantContext(arg.UserContext);
        var tenantRepository = tenantContext.GetTenantRepository();
        var graphQlUserContext = (GraphQlUserContext)arg.UserContext;

        var ckId = arg.FieldDefinition.GetMetadata<string>(Statics.CkId);

        var inputObjects = arg.GetArgument<List<MutationDto<RtEntityDto>>>(Statics.EntitiesArg);

        try
        {
            var entityUpdateInfos = new List<EntityUpdateInfo<RtEntity>>();
            var associationUpdateInfoList = new List<AssociationUpdateInfo>();
            foreach (var mutationDto in inputObjects)
            {
                var rtEntityId = new RtEntityId(ckId, mutationDto.RtId);
                var document = new RtEntity
                {
                    RtId = mutationDto.RtId,
                    CkTypeId = ckId
                };

                RtEntityFromInputObject(ckCacheService, graphQlUserContext.TenantId, document, mutationDto.Item, associationUpdateInfoList);
                entityUpdateInfos.Add(EntityUpdateInfo<RtEntity>.CreateUpdate(rtEntityId, document));
            }

            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(_sessionAccessor.Session, entityUpdateInfos,
                associationUpdateInfoList, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                foreach (var message in operationResult.Messages)
                {
                    if (message.MessageLevel == MessageLevel.Error)
                    {
                        arg.Errors.Add(new ExecutionError(message.MessageText)
                            { Code = string.Format(CommonConstants.GraphQLOperationError, message.MessageNumber) });
                    }
                    else if (message.MessageLevel == MessageLevel.FatalError)
                    {
                        arg.Errors.Add(new ExecutionError(message.MessageText)
                            { Code = string.Format(CommonConstants.GraphQLOperationFatalError, message.MessageNumber) });
                    }
                }

                return null;
            }

            return await GetResultSet(tenantRepository, ckId, entityUpdateInfos);
        }
        catch (OperationFailedException e)
        {
            arg.Errors.Add(new ExecutionError(e.Message, e) { Code = CommonConstants.GraphQLErrorDataStore });
            return null;
        }
        catch (Exception e)
        {
            arg.Errors.Add(new ExecutionError("A general error occurred", e)
                { Code = CommonConstants.GraphQLErrorCommon });
            return null;
        }
    }

    private async ValueTask<object?> ResolveCreate(IResolveFieldContext<object?> arg)
    {
        var ckCacheService = arg.RequestServices?.GetRequiredService<ICkCacheService>();
        if (ckCacheService == null)
        {
            throw AssetRepositoryException.ServiceNotRegistered(typeof(ICkCacheService));
        }

        var tenantContext = Helpers.GetTenantContext(arg.UserContext);
        var tenantRepository = tenantContext.GetTenantRepository();
        var graphQlUserContext = (GraphQlUserContext)arg.UserContext;

        var ckId = arg.FieldDefinition.GetMetadata<string>(Statics.CkId);

        var inputObjects = arg.GetArgument<List<RtEntityDto>>(Statics.EntitiesArg);

        try
        {
            var entityUpdateInfos = new List<EntityUpdateInfo<RtEntity>>();
            var associationUpdateInfoList = new List<AssociationUpdateInfo>();
            foreach (var rtEntityDto in inputObjects)
            {
                var rtEntity = await tenantRepository.CreateTransientRtEntityAsync(ckId);
                RtEntityFromInputObject(ckCacheService, graphQlUserContext.TenantId, rtEntity, rtEntityDto, associationUpdateInfoList);
                entityUpdateInfos.Add(EntityUpdateInfo<RtEntity>.CreateInsert(rtEntity));
            }

            var deleteAssociations =
                associationUpdateInfoList.Where(x => x.ModOption == AssociationModOptionsDto.Delete);
            if (deleteAssociations.Any())
            {
                arg.Errors.Add(new ExecutionError("Delete operations during creation are supported")
                    { Code = CommonConstants.GraphQLDeleteOperationsNotSupported });
                return null;
            }

            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(_sessionAccessor.Session, entityUpdateInfos,
                associationUpdateInfoList, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                foreach (var message in operationResult.Messages)
                {
                    if (message.MessageLevel == MessageLevel.Error)
                    {
                        arg.Errors.Add(new ExecutionError(message.MessageText)
                            { Code = string.Format(CommonConstants.GraphQLOperationError, message.MessageNumber) });
                    }
                    else if (message.MessageLevel == MessageLevel.FatalError)
                    {
                        arg.Errors.Add(new ExecutionError(message.MessageText)
                            { Code = string.Format(CommonConstants.GraphQLOperationFatalError, message.MessageNumber) });
                    }
                }

                return null;
            }

            return await GetResultSet(tenantRepository, ckId, entityUpdateInfos);
        }
        catch (OperationFailedException e)
        {
            arg.Errors.Add(new ExecutionError(e.Message, e) { Code = CommonConstants.GraphQLErrorDataStore });
            return null;
        }
        catch (Exception e)
        {
            arg.Errors.Add(new ExecutionError("A general error occurred", e)
                { Code = CommonConstants.GraphQLErrorCommon });
            return null;
        }
    }

    private async Task<IEnumerable<RtEntityDto>> GetResultSet(ITenantRepository repository, string ckId,
        List<EntityUpdateInfo<RtEntity>> entityUpdateInfos)
    {
        var resultSet = await repository.GetRtEntitiesByIdAsync(_sessionAccessor.Session, ckId,
            entityUpdateInfos.Select(x => x.RtEntityId.RtId).ToList(), DataQueryOperation.Create());

        return resultSet.Items.Select(RtEntityDtoType.CreateRtEntityDto);
    }

    private void RtEntityFromInputObject(ICkCacheService ckCacheService, string tenantId, RtEntity rtEntity, RtEntityDto rtEntityDto,
        List<AssociationUpdateInfo> associations)
    {
        var metaEntityCacheItem = ckCacheService.GetCkType(tenantId, rtEntity.CkTypeId);

        rtEntity.RtWellKnownName = rtEntityDto.RtWellKnownName;

        if (rtEntityDto.Properties != null)
        {
            foreach (var item in rtEntityDto.Properties)
            {
                if (TryHandleAttribute(rtEntity, metaEntityCacheItem, item))
                {
                    continue;
                }

                if (TryHandleInboundAssoc(rtEntity, metaEntityCacheItem, item, associations))
                {
                    continue;
                }

                TryHandleOutboundAssoc(rtEntity, metaEntityCacheItem, item, associations);
            }
        }
    }

    private bool TryHandleAttribute(RtEntity rtEntity, CkTypeGraph entityCacheItem,
        KeyValuePair<string, object> item)
    {
        var attributeName = item.Key;

        if (entityCacheItem.AllAttributesByName.TryGetValue(attributeName, out var attributeCacheItem))
        {
            rtEntity.SetAttributeValue(attributeCacheItem.AttributeName, attributeCacheItem.ValueType, item.Value);
            return true;
        }

        return false;
    }

    // ReSharper disable once UnusedMethodReturnValue.Local
    private bool TryHandleInboundAssoc(RtEntity rtEntity, CkTypeGraph entityCacheItem,
        KeyValuePair<string, object> item, List<AssociationUpdateInfo> associations)
    {
        var assocName = item.Key;

        var associationCacheItem = entityCacheItem.Associations.In.All
            .FirstOrDefault(a => a.NavigationPropertyName == assocName);
        if (associationCacheItem == null)
        {
            return false;
        }

        var rtAssociationInputDtos = (IEnumerable<object>)item.Value;
        foreach (RtAssociationInputDto rtAssociationDto in rtAssociationInputDtos)
        {
            var assocInfo = new AssociationUpdateInfo(
                rtAssociationDto.Target,
                rtEntity.ToRtEntityId(),
                associationCacheItem.CkRoleId,
                rtAssociationDto.ModOption ?? AssociationModOptionsDto.Create);
            associations.Add(assocInfo);
        }

        return true;
    }

    // ReSharper disable once UnusedMethodReturnValue.Local
    private bool TryHandleOutboundAssoc(RtEntity rtEntity, CkTypeGraph entityCacheItem,
        KeyValuePair<string, object> item, List<AssociationUpdateInfo> associations)
    {
        var assocName = item.Key;

        var associationCacheItem = entityCacheItem.Associations.Out.All
            .FirstOrDefault(a => a.NavigationPropertyName == assocName);
        if (associationCacheItem == null)
        {
            return false;
        }

        var rtAssociationInputDtos = (IEnumerable<object>)item.Value;
        foreach (RtAssociationInputDto rtAssociationDto in rtAssociationInputDtos)
        {
            var assocInfo = new AssociationUpdateInfo(
                rtEntity.ToRtEntityId(),
                rtAssociationDto.Target,
                associationCacheItem.CkRoleId,
                rtAssociationDto.ModOption ?? AssociationModOptionsDto.Create);
            associations.Add(assocInfo);
        }

        return true;
    }
}