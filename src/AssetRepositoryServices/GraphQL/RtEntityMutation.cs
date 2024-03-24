using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Messages;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

[DoNotRegister]
internal class RtEntityMutation : RtMutationBase
{
    public RtEntityMutation(IGraphTypesCache graphTypesCache, RtEntityDtoType rtEntityDtoType)
    {
        Name = rtEntityDtoType.CkTypeId.GetGraphQlPascalCaseName() + "Mutations";

        var inputType = graphTypesCache.GetInputType(rtEntityDtoType.CkTypeId);
        var outputType = graphTypesCache.GetType(rtEntityDtoType.CkTypeId);

        var createArgument = new QueryArgument(new NonNullGraphType(new ListGraphType(inputType)))
            { Name = Statics.EntitiesArg };
        var updateArgument =
            new QueryArgument(
                    new NonNullGraphType(new ListGraphType(new UpdateMutationDtoType<RtEntityDto>(inputType))))
                { Name = Statics.EntitiesArg };
        var deleteArgument =
            new QueryArgument(new NonNullGraphType(new ListGraphType(new OctoObjectIdType())))
                { Name = Statics.EntitiesArg };

        this.FieldAsync($"create", $"Creates new entities of type '{outputType.Name}'.",
                new ListGraphType(outputType),
                new QueryArguments(createArgument), ResolveCreate)
            .AddMetadata(Statics.CkId, rtEntityDtoType.CkTypeId);

        this.FieldAsync($"update", $"Updates existing entity of type '{outputType.Name}'.",
                new ListGraphType(outputType),
                new QueryArguments(updateArgument), ResolveUpdate)
            .AddMetadata(Statics.CkId, rtEntityDtoType.CkTypeId);

        this.FieldAsync($"delete", $"Deletes an entity of type '{outputType.Name}'.",
                new BooleanGraphType(),
                new QueryArguments(deleteArgument), ResolveDelete)
            .AddMetadata(Statics.CkId, rtEntityDtoType.CkTypeId);
    }

    private async ValueTask<object?> ResolveCreate(IResolveFieldContext<object?> arg)
    {
        var ckCacheService = arg.RequestServices?.GetRequiredService<ICkCacheService>();
        if (ckCacheService == null)
        {
            throw AssetRepositoryException.ServiceNotRegistered(typeof(ICkCacheService));
        }
        
        var sessionAccessor = arg.RequestServices?.GetRequiredService<IOctoSessionAccessor>();
        if (sessionAccessor?.Session == null)
        {
            throw AssetRepositoryException.SessionUnavailable();
        }

        var tenantContext = Helpers.GetTenantContext(arg.UserContext);
        var tenantRepository = tenantContext.GetTenantRepository();
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

        var inputObjects = arg.GetArgument<List<RtEntityDto>>(Statics.EntitiesArg);

        try
        {
            var entityUpdateInfos = new List<EntityUpdateInfo<RtEntity>>();
            var associationUpdateInfoList = new List<AssociationUpdateInfo>();
            foreach (var rtEntityDto in inputObjects)
            {
                var rtEntity = await tenantRepository.CreateTransientRtEntityAsync(ckTypeId);
                RtEntityFromInputObject(ckCacheService, graphQlUserContext.TenantId, rtEntity, rtEntityDto, associationUpdateInfoList);
                entityUpdateInfos.Add(EntityUpdateInfo<RtEntity>.CreateInsert(rtEntity));
            }

            var deleteAssociations =
                associationUpdateInfoList.Where(x => x.ModOption == AssociationModOptionsDto.Delete);
            if (deleteAssociations.Any())
            {
                arg.Errors.Add(new ExecutionError("Delete operations during creation are supported")
                    { Code = Statics.GraphQLDeleteOperationsNotSupported });
                return null;
            }

            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(sessionAccessor.Session, entityUpdateInfos,
                associationUpdateInfoList, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                foreach (var message in operationResult.Messages)
                {
                    if (message.MessageLevel == MessageLevel.Error)
                    {
                        arg.Errors.Add(new ExecutionError(message.MessageText)
                            { Code = string.Format(Statics.GraphQLOperationError, message.MessageNumber) });
                    }
                    else if (message.MessageLevel == MessageLevel.FatalError)
                    {
                        arg.Errors.Add(new ExecutionError(message.MessageText)
                            { Code = string.Format(Statics.GraphQLOperationFatalError, message.MessageNumber) });
                    }
                }

                return null;
            }

            return await GetResultSet(sessionAccessor.Session, tenantRepository, ckTypeId, entityUpdateInfos);
        }
        catch (PersistenceException e)
        {
            arg.Errors.Add(new ExecutionError(e.Message, e) { Code = Statics.GraphQLErrorDataStore });
            return null;
        }
        catch (Exception e)
        {
            arg.Errors.Add(new ExecutionError("A general error occurred", e)
                { Code = Statics.GraphQLErrorCommon });
            return null;
        }
    }


    private async ValueTask<object?> ResolveDelete(IResolveFieldContext<object?> arg)
    {
        var tenantContext = Helpers.GetTenantContext(arg.UserContext);
        var tenantRepository = tenantContext.GetTenantRepository();
        
        var sessionAccessor = arg.RequestServices?.GetRequiredService<IOctoSessionAccessor>();
        if (sessionAccessor?.Session == null)
        {
            throw AssetRepositoryException.SessionUnavailable();
        }

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

        var inputObjects = arg.GetArgument<List<OctoObjectId>>(Statics.EntitiesArg);

        try
        {
            var entityUpdateInfos = new List<EntityUpdateInfo<RtEntity>>();
            foreach (var rtId in inputObjects)
            {
                entityUpdateInfos.Add(EntityUpdateInfo<RtEntity>.CreateDelete(new RtEntityId(ckTypeId, rtId)));
            }

            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(sessionAccessor.Session, entityUpdateInfos, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                return false;
            }

            return true;
        }
        catch (OperationFailedException e)
        {
            arg.Errors.Add(new ExecutionError(e.Message, e) { Code = Statics.GraphQLErrorDataStore });
            return false;
        }
        catch (Exception e)
        {
            arg.Errors.Add(new ExecutionError("A general error occurred", e)
                { Code = Statics.GraphQLErrorCommon });
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
                
        var sessionAccessor = arg.RequestServices?.GetRequiredService<IOctoSessionAccessor>();
        if (sessionAccessor?.Session == null)
        {
            throw AssetRepositoryException.SessionUnavailable();
        }

        var tenantContext = Helpers.GetTenantContext(arg.UserContext);
        var tenantRepository = tenantContext.GetTenantRepository();
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

        var inputObjects = arg.GetArgument<List<MutationDto<RtEntityDto>>>(Statics.EntitiesArg);

        try
        {
            var entityUpdateInfos = new List<EntityUpdateInfo<RtEntity>>();
            var associationUpdateInfoList = new List<AssociationUpdateInfo>();
            foreach (var mutationDto in inputObjects)
            {
                var rtEntityId = new RtEntityId(ckTypeId, mutationDto.RtId);
                var document = new RtEntity
                {
                    RtId = mutationDto.RtId,
                    CkTypeId = ckTypeId
                };

                RtEntityFromInputObject(ckCacheService, graphQlUserContext.TenantId, document, mutationDto.Item, associationUpdateInfoList);
                entityUpdateInfos.Add(EntityUpdateInfo<RtEntity>.CreateUpdate(rtEntityId, document));
            }

            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(sessionAccessor.Session, entityUpdateInfos,
                associationUpdateInfoList, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                foreach (var message in operationResult.Messages)
                {
                    if (message.MessageLevel == MessageLevel.Error)
                    {
                        arg.Errors.Add(new ExecutionError(message.MessageText)
                            { Code = string.Format(Statics.GraphQLOperationError, message.MessageNumber) });
                    }
                    else if (message.MessageLevel == MessageLevel.FatalError)
                    {
                        arg.Errors.Add(new ExecutionError(message.MessageText)
                            { Code = string.Format(Statics.GraphQLOperationFatalError, message.MessageNumber) });
                    }
                }

                return null;
            }

            return await GetResultSet(sessionAccessor.Session, tenantRepository, ckTypeId, entityUpdateInfos);
        }
        catch (OperationFailedException e)
        {
            arg.Errors.Add(new ExecutionError(e.Message, e) { Code = Statics.GraphQLErrorDataStore });
            return null;
        }
        catch (Exception e)
        {
            arg.Errors.Add(new ExecutionError("A general error occurred", e)
                { Code = Statics.GraphQLErrorCommon });
            return null;
        }
    }

}