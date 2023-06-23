using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GraphQL;
using GraphQL.Resolvers;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;
using Meshmakers.Octo.SystematizedData.Persistence;
using Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine;
using Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using Microsoft.AspNetCore.Http;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

/// <summary>
///     Implements mutations of Octo
/// </summary>
public sealed class OctoMutation : ObjectGraphType
{
    private readonly ICkCache _ckCache;
    private readonly IOctoSessionAccessor _sessionAccessor;

    internal OctoMutation(IEnumerable<EntityCacheItem> entityCacheItems, IGraphTypesCache graphTypesCache,
        IOctoSessionAccessor sessionAccessor, ICkCache ckCache)
    {
        _sessionAccessor = sessionAccessor;
        _ckCache = ckCache;
        foreach (var cacheItem in entityCacheItems)
        {
            var inputType = graphTypesCache.GetOrCreateInput(cacheItem.CkId);
            var outputType = graphTypesCache.GetOrCreate(cacheItem.CkId);

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
                .AddMetadata(Statics.CkId, cacheItem.CkId);

            this.FieldAsync($"update{outputType.Name}s", $"Updates existing entity of type '{outputType.Name}'.",
                    new ListGraphType(outputType),
                    new QueryArguments(updateArgument), ResolveUpdate)
                .AddMetadata(Statics.CkId, cacheItem.CkId);

            this.FieldAsync($"delete{outputType.Name}s", $"Deletes an entity of type '{outputType.Name}'.",
                    new BooleanGraphType(),
                    new QueryArguments(deleteArgument), ResolveDelete)
                .AddMetadata(Statics.CkId, cacheItem.CkId);
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

        var file = context.GetArgument<IFormFile>(Statics.LargeBinaryDataArg);
        var fileName = file.FileName;
        var contentType = file.ContentType;

        return await tenantContext.Repository.UploadLargeBinaryAsync(fileName, contentType, file.OpenReadStream(),
            CancellationToken.None);
    }

    private async ValueTask<object> ResolveDelete(IResolveFieldContext<object> arg)
    {
        var graphQlUserContext = (GraphQLUserContext)arg.UserContext;

        var ckId = arg.FieldDefinition.GetMetadata<string>(Statics.CkId);

        var inputObjects = arg.GetArgument<List<MutationDto<object>>>(Statics.EntitiesArg);

        try
        {
            var entityUpdateInfos = new List<EntityUpdateInfo>();
            foreach (var mutationDto in inputObjects)
            {
                var document = new RtEntity
                {
                    RtId = mutationDto.RtId.ToObjectId(),
                    CkId = ckId
                };

                entityUpdateInfos.Add(new EntityUpdateInfo(document, EntityModOptions.Delete));
            }

            await graphQlUserContext.TenantContext.Repository.ApplyChanges(_sessionAccessor.Session, entityUpdateInfos);
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

    private async ValueTask<object> ResolveUpdate(IResolveFieldContext<object> arg)
    {
        var graphQlUserContext = (GraphQLUserContext)arg.UserContext;

        var ckId = arg.FieldDefinition.GetMetadata<string>(Statics.CkId);

        var inputObjects = arg.GetArgument<List<MutationDto<RtEntityDto>>>(Statics.EntitiesArg);

        try
        {
            var entityUpdateInfos = new List<EntityUpdateInfo>();
            var associationUpdateInfoList = new List<AssociationUpdateInfo>();
            foreach (var mutationDto in inputObjects)
            {
                var document = new RtEntity
                {
                    RtId = mutationDto.RtId.ToObjectId(),
                    CkId = ckId
                };

                RtEntityFromInputObject(document, mutationDto.Item, associationUpdateInfoList);
                entityUpdateInfos.Add(new EntityUpdateInfo(document, EntityModOptions.Update));
            }

            await graphQlUserContext.TenantContext.Repository.ApplyChanges(_sessionAccessor.Session, entityUpdateInfos,
                associationUpdateInfoList);

            return await GetResultSet(graphQlUserContext.TenantContext.Repository, ckId, entityUpdateInfos);
        }
        catch (CkModelViolationException e)
        {
            arg.Errors.Add(new ExecutionError(e.Message, e) { Code = CommonConstants.GraphQLCkModelViolation });
            return null;
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

    private async ValueTask<object> ResolveCreate(IResolveFieldContext<object> arg)
    {
        var graphQlUserContext = (GraphQLUserContext)arg.UserContext;

        var ckId = arg.FieldDefinition.GetMetadata<string>(Statics.CkId);

        var inputObjects = arg.GetArgument<List<RtEntityDto>>(Statics.EntitiesArg);

        try
        {
            var entityUpdateInfos = new List<EntityUpdateInfo>();
            var associationUpdateInfoList = new List<AssociationUpdateInfo>();
            foreach (var rtEntityDto in inputObjects)
            {
                var rtEntity = graphQlUserContext.TenantContext.Repository.CreateTransientRtEntity(ckId);
                RtEntityFromInputObject(rtEntity, rtEntityDto, associationUpdateInfoList);
                entityUpdateInfos.Add(new EntityUpdateInfo(rtEntity, EntityModOptions.Create));
            }

            var deleteAssociations =
                associationUpdateInfoList.Where(x => x.ModOption == AssociationModOptionsDto.Delete);
            if (deleteAssociations.Any())
            {
                arg.Errors.Add(new ExecutionError("Delete operations during creation are supported")
                    { Code = CommonConstants.GraphQLDeleteOperationsNotSupported });
                return null;
            }

            await graphQlUserContext.TenantContext.Repository.ApplyChanges(_sessionAccessor.Session, entityUpdateInfos,
                associationUpdateInfoList);

            return await GetResultSet(graphQlUserContext.TenantContext.Repository, ckId, entityUpdateInfos);
        }
        catch (CkModelViolationException e)
        {
            arg.Errors.Add(new ExecutionError(e.Message, e) { Code = CommonConstants.GraphQLCkModelViolation });
            return null;
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
        List<EntityUpdateInfo> entityUpdateInfos)
    {
        var resultSet = await repository.GetRtEntitiesByIdAsync(_sessionAccessor.Session, ckId,
            entityUpdateInfos.Select(x => x.RtEntity.RtId).ToList(), new DataQueryOperation());

        return resultSet.Result.Select(RtEntityDtoType.CreateRtEntityDto);
    }

    private void RtEntityFromInputObject(RtEntity rtEntity, RtEntityDto rtEntityDto,
        List<AssociationUpdateInfo> associations)
    {
        var metaEntityCacheItem = _ckCache.GetEntityCacheItem(rtEntity.CkId);

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

    private bool TryHandleAttribute(RtEntity rtEntity, EntityCacheItem entityCacheItem,
        KeyValuePair<string, object> item)
    {
        var attributeName = item.Key;

        var attributeCacheItem =
            entityCacheItem.Attributes.Values.FirstOrDefault(a => a.AttributeName == attributeName);
        if (attributeCacheItem == null)
        {
            return false;
        }

        rtEntity.SetAttributeValue(attributeCacheItem.AttributeName, attributeCacheItem.AttributeValueType, item.Value);
        return true;
    }

    // ReSharper disable once UnusedMethodReturnValue.Local
    private bool TryHandleInboundAssoc(RtEntity rtEntity, EntityCacheItem entityCacheItem,
        KeyValuePair<string, object> item, List<AssociationUpdateInfo> associations)
    {
        var assocName = item.Key;

        var associationCacheItem = entityCacheItem.InboundAssociations.Values.SelectMany(x => x)
            .FirstOrDefault(a => a.Name == assocName);
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
                associationCacheItem.RoleId,
                rtAssociationDto.ModOption ?? AssociationModOptionsDto.Create);
            associations.Add(assocInfo);
        }

        return true;
    }

    // ReSharper disable once UnusedMethodReturnValue.Local
    private bool TryHandleOutboundAssoc(RtEntity rtEntity, EntityCacheItem entityCacheItem,
        KeyValuePair<string, object> item, List<AssociationUpdateInfo> associations)
    {
        var assocName = item.Key;

        var associationCacheItem = entityCacheItem.OutboundAssociations.Values.SelectMany(x => x)
            .FirstOrDefault(a => a.Name == assocName);
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
                associationCacheItem.RoleId,
                rtAssociationDto.ModOption ?? AssociationModOptionsDto.Create);
            associations.Add(assocInfo);
        }

        return true;
    }
}
