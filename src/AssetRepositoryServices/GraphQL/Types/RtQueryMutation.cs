using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
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

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class RtQueryMutation : RtMutationBase
{
    public RtQueryMutation()
    {
        Name = "RtQueryMutations";

        
        Field<NonNullGraphType<ListGraphType<NonNullGraphType<RtQueryRowDtoType>>>>("create")
            .Description("Create entities of a runtime query.")
            .Argument<NonNullGraphType<ListGraphType<NonNullGraphType<RtQueryRowDtoInputType>>>>(Statics.EntitiesArg)
            .ResolveAsync(ResolveCreate);
        Field<NonNullGraphType<ListGraphType<NonNullGraphType<RtQueryRowDtoType>>>>("update")
            .Description("Updates entities of a runtime query.")
            .Argument<NonNullGraphType<ListGraphType<NonNullGraphType<RtQueryRowDtoUpdateType>>>>(Statics.EntitiesArg)
            .ResolveAsync(ResolveUpdate);
        Field<NonNullGraphType<BooleanGraphType>>("delete")
            .Description("Deletes entities of a runtime query.")
            .Argument<NonNullGraphType<ListGraphType<NonNullGraphType<RtEntityIdType>>>>(Statics.EntitiesArg)
            .ResolveAsync(ResolveDelete);        
    }

    private async Task<object?> ResolveCreate(IResolveFieldContext<object?> context)
    {
        var ckCacheService = context.RequestServices?.GetRequiredService<ICkCacheService>();
        if (ckCacheService == null)
        {
            throw AssetRepositoryException.ServiceNotRegistered(typeof(ICkCacheService));
        }
        
        var sessionAccessor = context.RequestServices?.GetRequiredService<IOctoSessionAccessor>();
        if (sessionAccessor?.Session == null)
        {
            throw AssetRepositoryException.SessionUnavailable();
        }

        if (context.Parent == null)
        {
            throw AssetRepositoryException.ParentUnavailable();
        }

        var tenantContext = Helpers.GetTenantContext(context.UserContext);
        var tenantRepository = tenantContext.GetTenantRepository();
        var graphQlUserContext = (GraphQlUserContext)context.UserContext;
        
        var queryRtId = context.Parent.GetArgument<OctoObjectId>(Statics.RtIdArg);
        var inputObjects = context.GetArgument<List<RtQueryRowDto>>(Statics.EntitiesArg);

        try
        {
            var entityUpdateInfos = new List<EntityUpdateInfo<RtEntity>>();
            foreach (var queryRowDto in inputObjects)
            {
                var rtEntity = await tenantRepository.CreateTransientRtEntityAsync(queryRowDto.CkTypeId);
                await RtEntityFromInputObjectAsync(ckCacheService, graphQlUserContext.TenantId, rtEntity, queryRowDto);
                entityUpdateInfos.Add(EntityUpdateInfo<RtEntity>.CreateInsert(rtEntity));
            }

            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(sessionAccessor.Session, entityUpdateInfos, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                foreach (var message in operationResult.Messages)
                {
                    if (message.MessageLevel == MessageLevel.Error)
                    {
                        context.Errors.Add(new ExecutionError(message.MessageText)
                            { Code = string.Format(Statics.GraphQlOperationError, message.MessageNumber) });
                    }
                    else if (message.MessageLevel == MessageLevel.FatalError)
                    {
                        context.Errors.Add(new ExecutionError(message.MessageText)
                            { Code = string.Format(Statics.GraphQlOperationFatalError, message.MessageNumber) });
                    }
                }

                return null;
            }

            return await GetRtQueryRowResultSet(sessionAccessor.Session, ckCacheService, tenantRepository, entityUpdateInfos, queryRtId);
        }
        catch (PersistenceException e)
        {
            context.Errors.Add(new ExecutionError(e.Message, e) { Code = Statics.GraphQlErrorDataStore });
            return null;
        }
        catch (Exception e)
        {
            context.Errors.Add(new ExecutionError("A general error occurred", e)
                { Code = Statics.GraphQlErrorCommon });
            return null;
        }
    }

    private async Task<object?> ResolveDelete(IResolveFieldContext<object?> context)
    {
        var tenantContext = Helpers.GetTenantContext(context.UserContext);
        var tenantRepository = tenantContext.GetTenantRepository();
        
        var sessionAccessor = context.RequestServices?.GetRequiredService<IOctoSessionAccessor>();
        if (sessionAccessor?.Session == null)
        {
            throw AssetRepositoryException.SessionUnavailable();
        }

        var inputObjects = context.GetArgument<List<RtEntityIdDto>>(Statics.EntitiesArg);

        try
        {
            var entityUpdateInfos = new List<EntityUpdateInfo<RtEntity>>();
            foreach (var rtEntityId in inputObjects)
            {
                entityUpdateInfos.Add(EntityUpdateInfo<RtEntity>.CreateDelete(new RtEntityId(rtEntityId.CkTypeId, rtEntityId.RtId)));
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
            context.Errors.Add(new ExecutionError(e.Message, e) { Code = Statics.GraphQlErrorDataStore });
            return false;
        }
        catch (Exception e)
        {
            context.Errors.Add(new ExecutionError("A general error occurred", e)
                { Code = Statics.GraphQlErrorCommon });
            return false;
        }
    }

    private async Task<object?> ResolveUpdate(IResolveFieldContext<object?> context)
    {
        var ckCacheService = context.RequestServices?.GetRequiredService<ICkCacheService>();
        if (ckCacheService == null)
        {
            throw AssetRepositoryException.ServiceNotRegistered(typeof(ICkCacheService));
        }
                
        var sessionAccessor = context.RequestServices?.GetRequiredService<IOctoSessionAccessor>();
        if (sessionAccessor?.Session == null)
        {
            throw AssetRepositoryException.SessionUnavailable();
        }

        if (context.Parent == null)
        {
            throw AssetRepositoryException.ParentUnavailable();
        }
        
        var tenantContext = Helpers.GetTenantContext(context.UserContext);
        var tenantRepository = tenantContext.GetTenantRepository();
        var graphQlUserContext = (GraphQlUserContext)context.UserContext;

        var queryRtId = context.Parent.GetArgument<OctoObjectId>(Statics.RtIdArg);
        var inputObjects = context.GetArgument<List<MutationDto<RtQueryRowDto>>>(Statics.EntitiesArg);

        try
        {
            var entityUpdateInfos = new List<EntityUpdateInfo<RtEntity>>();
            foreach (var mutationDto in inputObjects)
            {
                var rtEntityId = new RtEntityId(mutationDto.Item.CkTypeId, mutationDto.RtId);
                var document = new RtEntity
                {
                    RtId = mutationDto.RtId,
                    CkTypeId = mutationDto.Item.CkTypeId
                };

                await RtEntityFromInputObjectAsync(ckCacheService, graphQlUserContext.TenantId, document, mutationDto.Item);
                if (document.Attributes.Any())
                {
                    entityUpdateInfos.Add(EntityUpdateInfo<RtEntity>.CreateUpdate(rtEntityId, document));
                }
            }

            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(sessionAccessor.Session, entityUpdateInfos,
                operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                foreach (var message in operationResult.Messages)
                {
                    if (message.MessageLevel == MessageLevel.Error)
                    {
                        context.Errors.Add(new ExecutionError(message.MessageText)
                            { Code = string.Format(Statics.GraphQlOperationError, message.MessageNumber) });
                    }
                    else if (message.MessageLevel == MessageLevel.FatalError)
                    {
                        context.Errors.Add(new ExecutionError(message.MessageText)
                            { Code = string.Format(Statics.GraphQlOperationFatalError, message.MessageNumber) });
                    }
                }

                return null;
            }

            return await GetRtQueryRowResultSet(sessionAccessor.Session, ckCacheService, tenantRepository, entityUpdateInfos, queryRtId);
        }
        catch (OperationFailedException e)
        {
            context.Errors.Add(new ExecutionError(e.Message, e) { Code = Statics.GraphQlErrorDataStore });
            return null;
        }
        catch (Exception e)
        {
            context.Errors.Add(new ExecutionError("A general error occurred", e)
                { Code = Statics.GraphQlErrorCommon });
            return null;
        }
    }

}