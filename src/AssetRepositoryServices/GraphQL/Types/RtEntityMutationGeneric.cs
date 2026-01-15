using AssetRepositoryServices.Resources;
using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

internal sealed class RtEntityMutationGeneric : RtMutationBase
{
    public RtEntityMutationGeneric()
    {
        Name = "RtEntityMutations";

        // Create mutation
        var createArgument =
            new QueryArgument(typeof(NonNullGraphType<ListGraphType<RtEntityDtoGenericInputType>>))
                { Name = Statics.EntitiesArg, Description = AssetTexts.Graphql_Arguments_Entities_Description };
        this.FieldAsync("create",
            AssetTexts.Graphql_RtEntityMutationGeneric_CreateOperation_Description,
            new ListGraphType(new RtEntityGenericDtoType()),
            new QueryArguments(createArgument), ResolveCreate);

        // Update mutation
        var updateArgument =
            new QueryArgument(typeof(NonNullGraphType<ListGraphType<RtEntityDtoGenericUpdateType>>))
                { Name = Statics.EntitiesArg, Description = AssetTexts.Graphql_Arguments_Entities_Description };
        this.FieldAsync("update",
            AssetTexts.Graphql_RtEntityMutationGeneric_UpdateOperation_Description,
            new ListGraphType(new RtEntityGenericDtoType()),
            new QueryArguments(updateArgument), ResolveUpdate);

        // Delete mutation
        var deleteArgument =
            new QueryArgument(typeof(NonNullGraphType<ListGraphType<RtEntityIdType>>))
                { Name = Statics.EntitiesArg, Description = AssetTexts.Graphql_Arguments_Entities_Description };
        var strategyArgument =
            new QueryArgument(typeof(DeleteOptionsDtoType))
            {
                Name = Statics.OptionsArg, DefaultValue = DeleteStrategies.Archive,
                Description = AssetTexts.Graphql_Arguments_Options_Description
            };
        this.FieldAsync("delete",
            AssetTexts.Graphql_RtEntityMutationGeneric_DeleteOperation_Description,
            new BooleanGraphType(),
            new QueryArguments(deleteArgument, strategyArgument), ResolveDelete);
    }

    private async ValueTask<object?> ResolveCreate(IResolveFieldContext<object?> arg)
    {
        var ckCacheService = arg.GetCkCacheService();
        var sessionAccessor = arg.GetSessionAccessor();

        var tenantContext = Helpers.GetTenantContext(arg.UserContext);
        var tenantRepository = tenantContext.GetTenantRepository();
        var graphQlUserContext = (GraphQlUserContext)arg.UserContext;

        var inputObjects = arg.GetArgument<List<RtEntityDto>>(Statics.EntitiesArg);

        if (inputObjects.Count == 0)
        {
            return new List<RtEntityDto>();
        }

        // Validate all entities have the same CkTypeId
        var firstCkTypeId = inputObjects[0].CkTypeId;
        if (inputObjects.Any(e => !e.CkTypeId.Equals(firstCkTypeId)))
        {
            arg.Errors.Add(new ExecutionError("All entities must have the same CkTypeId.")
                { Code = Statics.GraphQlInvalidArguments });
            return null;
        }

        try
        {
            var entityUpdateInfos = new List<EntityUpdateInfo<RtEntity>>();
            var associationUpdateInfoList = new List<AssociationUpdateInfo>();
            foreach (var rtEntityDto in inputObjects)
            {
                var rtEntity = await tenantRepository.CreateTransientRtEntityByRtCkIdAsync(rtEntityDto.CkTypeId);
                await RtEntityFromInputObjectAsync(ckCacheService, graphQlUserContext.TenantId, rtEntity, rtEntityDto,
                    associationUpdateInfoList);
                entityUpdateInfos.Add(EntityUpdateInfo<RtEntity>.CreateInsert(rtEntity));
            }

            var deleteAssociations =
                associationUpdateInfoList.Where(x => x.ModOption == AssociationModOptionsDto.Delete);
            if (deleteAssociations.Any())
            {
                arg.Errors.Add(new ExecutionError("Delete operations during creation are not supported.")
                    { Code = Statics.GraphQlDeleteOperationsNotSupported });
                return null;
            }

            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(sessionAccessor.Session, entityUpdateInfos,
                associationUpdateInfoList, operationResult);
            ResolveConnectionContextExtensions.ValidateOperationResult(operationResult);

            return await GetResultSet(sessionAccessor.Session, tenantRepository, entityUpdateInfos);
        }
        catch (Exception e)
        {
            return arg.HandleException(e);
        }
    }

    private async ValueTask<object?> ResolveUpdate(IResolveFieldContext<object?> arg)
    {
        var ckCacheService = arg.GetCkCacheService();
        var sessionAccessor = arg.GetSessionAccessor();

        var tenantContext = Helpers.GetTenantContext(arg.UserContext);
        var tenantRepository = tenantContext.GetTenantRepository();
        var graphQlUserContext = (GraphQlUserContext)arg.UserContext;

        var inputObjects = arg.GetArgument<List<MutationDto<RtEntityDto>>>(Statics.EntitiesArg);

        if (inputObjects.Count == 0)
        {
            return new List<RtEntityDto>();
        }

        // Validate all entities have the same CkTypeId
        var firstCkTypeId = inputObjects[0].Item.CkTypeId;
        if (inputObjects.Any(e => !e.Item.CkTypeId.Equals(firstCkTypeId)))
        {
            arg.Errors.Add(new ExecutionError("All entities must have the same CkTypeId.")
                { Code = Statics.GraphQlInvalidArguments });
            return null;
        }

        try
        {
            var entityUpdateInfos = new List<EntityUpdateInfo<RtEntity>>();
            var associationUpdateInfoList = new List<AssociationUpdateInfo>();
            foreach (var mutationDto in inputObjects)
            {
                var ckTypeId = mutationDto.Item.CkTypeId;
                var rtEntityId = new RtEntityId(ckTypeId, mutationDto.RtId);
                var document = new RtEntity
                {
                    RtId = mutationDto.RtId,
                    CkTypeId = ckTypeId
                };

                await RtEntityFromInputObjectAsync(ckCacheService, graphQlUserContext.TenantId, document,
                    mutationDto.Item, associationUpdateInfoList);
                if (document.Attributes.Any())
                {
                    entityUpdateInfos.Add(EntityUpdateInfo<RtEntity>.CreateUpdate(rtEntityId, document));
                }
            }

            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(sessionAccessor.Session, entityUpdateInfos,
                associationUpdateInfoList, operationResult);
            ResolveConnectionContextExtensions.ValidateOperationResult(operationResult);

            return await GetResultSet(sessionAccessor.Session, tenantRepository, entityUpdateInfos);
        }
        catch (Exception e)
        {
            return arg.HandleException(e);
        }
    }

    private async ValueTask<object?> ResolveDelete(IResolveFieldContext<object?> arg)
    {
        var tenantContext = Helpers.GetTenantContext(arg.UserContext);
        var tenantRepository = tenantContext.GetTenantRepository();
        var sessionAccessor = arg.GetSessionAccessor();

        var inputObjects = arg.GetArgument<List<RtEntityIdDto>>(Statics.EntitiesArg);
        arg.TryGetArgument(Statics.OptionsArg, DeleteStrategies.Archive, out var deleteStrategy);

        try
        {
            var entityUpdateInfos = new List<EntityUpdateInfo<RtEntity>>();
            foreach (var rtEntityIdDto in inputObjects)
            {
                entityUpdateInfos.Add(
                    EntityUpdateInfo<RtEntity>.CreateDelete(new RtEntityId(rtEntityIdDto.CkTypeId,
                        rtEntityIdDto.RtId)));
            }

            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(sessionAccessor.Session, entityUpdateInfos,
                new DeleteOptions { Strategy = deleteStrategy },
                operationResult);
            ResolveConnectionContextExtensions.ValidateOperationResult(operationResult);

            return true;
        }
        catch (Exception e)
        {
            return arg.HandleException(e);
        }
    }
}