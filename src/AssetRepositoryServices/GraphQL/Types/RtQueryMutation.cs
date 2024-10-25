using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Messages;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class RtQueryMutation : RtMutationBase
{
    public RtQueryMutation()
    {
        Name = "RtQueryMutations";

        
        Field<ListGraphType<RtEntityGenericDtoType>>("create")
            .Description("Create entities of a runtime query.")
            .Argument<ListGraphType<RtEntityDtoGenericInputType>>(Statics.EntitiesArg)
            .ResolveAsync(ResolveCreate);
        Field<ListGraphType<RtEntityGenericDtoType>>("update")
            .Description("Updates entities of a runtime query.")
            .Argument<ListGraphType<UpdateMutationDtoGeneric>>(Statics.EntitiesArg)
            .ResolveAsync(ResolveUpdate);
        Field<ListGraphType<RtEntityGenericDtoType>>("delete")
            .Description("Deletes entities of a runtime query.")
            .ResolveAsync(ResolveDelete);        
    }

    private async Task<object?> ResolveCreate(IResolveFieldContext<object?> arg)
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


        var inputObjects = arg.GetArgument<List<RtEntityDto>>(Statics.EntitiesArg);

        try
        {
            var entityUpdateInfos = new List<EntityUpdateInfo<RtEntity>>();
            var associationUpdateInfoList = new List<AssociationUpdateInfo>();
            foreach (var rtEntityDto in inputObjects)
            {
                var rtEntity = await tenantRepository.CreateTransientRtEntityAsync(rtEntityDto.CkTypeId);
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

            return await GetResultSet(sessionAccessor.Session, tenantRepository, entityUpdateInfos);
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

    private async Task<object?> ResolveDelete(IResolveFieldContext<object?> arg)
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

    private async Task<object?> ResolveUpdate(IResolveFieldContext<object?> arg)
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
        
        var inputObjects = arg.GetArgument<List<MutationDto<RtEntityDto>>>(Statics.EntitiesArg);

        try
        {
            var entityUpdateInfos = new List<EntityUpdateInfo<RtEntity>>();
            var associationUpdateInfoList = new List<AssociationUpdateInfo>();
            foreach (var mutationDto in inputObjects)
            {
                var rtEntityId = new RtEntityId(mutationDto.Item.CkTypeId, mutationDto.RtId);
                var document = new RtEntity
                {
                    RtId = mutationDto.RtId,
                    CkTypeId = mutationDto.Item.CkTypeId
                };

                RtEntityFromInputObject(ckCacheService, graphQlUserContext.TenantId, document, mutationDto.Item, associationUpdateInfoList);
                if (document.Attributes.Any())
                {
                    entityUpdateInfos.Add(EntityUpdateInfo<RtEntity>.CreateUpdate(rtEntityId, document));
                }
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

            return await GetResultSet(sessionAccessor.Session, tenantRepository, entityUpdateInfos);
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