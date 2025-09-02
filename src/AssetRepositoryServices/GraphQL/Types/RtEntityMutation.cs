using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Messages;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using MongoDB.Driver;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

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

        this.FieldAsync("create", $"Creates new entities of type '{outputType.Name}'.",
                new ListGraphType(outputType),
                new QueryArguments(createArgument), ResolveCreate)
            .AddMetadata(Statics.CkId, rtEntityDtoType.CkTypeId);

        this.FieldAsync("update", $"Updates existing entity of type '{outputType.Name}'.",
                new ListGraphType(outputType),
                new QueryArguments(updateArgument), ResolveUpdate)
            .AddMetadata(Statics.CkId, rtEntityDtoType.CkTypeId);
    }

    private async ValueTask<object?> ResolveCreate(IResolveFieldContext<object?> arg)
    {
        var ckCacheService = arg.GetCkCacheService();
        var sessionAccessor = arg.GetSessionAccessor();

        var tenantContext = Helpers.GetTenantContext(arg.UserContext);
        var tenantRepository = tenantContext.GetTenantRepository();
        var graphQlUserContext = (GraphQlUserContext)arg.UserContext;

        if (!arg.FieldDefinition.Metadata.TryGetValue(Statics.CkId, out var ckIdObj))
        {
            arg.Errors.Add(new ExecutionError("Invalid query. Missing construction kit id.")
                { Code = Statics.GraphQlInvalidArguments });
            return null;
        }

        if (ckIdObj is not CkId<CkTypeId> ckTypeId)
        {
            arg.Errors.Add(new ExecutionError("Invalid query. Invalid construction kit id.")
                { Code = Statics.GraphQlInvalidArguments });
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
                await RtEntityFromInputObjectAsync(ckCacheService, graphQlUserContext.TenantId, rtEntity, rtEntityDto,
                    associationUpdateInfoList);
                entityUpdateInfos.Add(EntityUpdateInfo<RtEntity>.CreateInsert(rtEntity));
            }

            var deleteAssociations =
                associationUpdateInfoList.Where(x => x.ModOption == AssociationModOptionsDto.Delete);
            if (deleteAssociations.Any())
            {
                arg.Errors.Add(new ExecutionError("Delete operations during creation are supported")
                    { Code = Statics.GraphQlDeleteOperationsNotSupported });
                return null;
            }

            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(sessionAccessor.Session, entityUpdateInfos,
                associationUpdateInfoList, operationResult);
            arg.ValidateOperationResult(operationResult);

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

        var ckTypeId = arg.GetMetadataValue<CkId<CkTypeId>>(Statics.CkId);


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
            arg.ValidateOperationResult(operationResult);

            return await GetResultSet(sessionAccessor.Session, tenantRepository, entityUpdateInfos);
        }
        catch (Exception e)
        {
            return arg.HandleException(e);
        }
    }
}