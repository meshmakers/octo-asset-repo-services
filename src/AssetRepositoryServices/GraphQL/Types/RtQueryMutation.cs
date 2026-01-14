using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Mapping;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Messages;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class RtQueryMutation : RtMutationBase
{
    public RtQueryMutation()
    {
        Name = "RtQueryMutations";


        Field<ListGraphType<NonNullGraphType<RtQueryRowDtoType>>>("create")
            .Description("Create entities of a runtime query.")
            .Argument<NonNullGraphType<ListGraphType<NonNullGraphType<RtQueryRowDtoInputType>>>>(Statics.EntitiesArg)
            .ResolveAsync(ResolveInsert);
        Field<ListGraphType<NonNullGraphType<RtQueryRowDtoType>>>("update")
            .Description("Updates entities of a runtime query.")
            .Argument<NonNullGraphType<ListGraphType<NonNullGraphType<RtQueryRowDtoUpdateType>>>>(Statics.EntitiesArg)
            .ResolveAsync(ResolveUpdate);
        Field<BooleanGraphType>("delete")
            .Description("Deletes entities of a runtime query.")
            .Argument<NonNullGraphType<ListGraphType<NonNullGraphType<RtEntityIdType>>>>(Statics.EntitiesArg)
            .ResolveAsync(ResolveDelete);
    }

    private async Task<object?> ResolveInsert(IResolveFieldContext<object?> context)
    {
        try
        {
            var ckCacheService = context.GetCkCacheService();
            var sessionAccessor = context.GetSessionAccessor();

            if (context.Parent == null)
            {
                throw AssetRepositoryException.ParentUnavailable();
            }

            var tenantContext = Helpers.GetTenantContext(context.UserContext);
            var tenantRepository = tenantContext.GetTenantRepository();
            var graphQlUserContext = (GraphQlUserContext)context.UserContext;

            var queryRtId = context.Parent.GetArgument<OctoObjectId>(Statics.RtIdArg);
            var inputObjects = context.GetArgument<List<RtSimpleQueryRowDto>>(Statics.EntitiesArg);

            var rtQuery = await tenantRepository.GetRtEntityByRtIdAsync<RtSimpleRtQuery>(sessionAccessor.Session, queryRtId);
            if (rtQuery == null)
            {
                throw AssetRepositoryException.QueryNotFound(queryRtId);
            }

            QueryMapperEngine queryMapperEngine = new QueryMapperEngine();
            var queryMapper = await queryMapperEngine.CreateQueryMapperAsync(ckCacheService, graphQlUserContext,
                rtQuery, tenantRepository, inputObjects, sessionAccessor);


            var entityUpdateInfos = new List<EntityUpdateInfo<RtEntity>>();
            var associationUpdateInfoList = new List<AssociationUpdateInfo>();

            var mappingResult = new MappingResult();

            foreach (var queryRowDto in inputObjects)
            {
                var rtEntity = await tenantRepository.CreateTransientRtEntityByRtCkIdAsync(queryRowDto.CkTypeId);

                await queryMapper.MapAsync(rtEntity, queryRowDto, associationUpdateInfoList, MappingMode.Insert,
                    mappingResult);

                entityUpdateInfos.Add(EntityUpdateInfo<RtEntity>.CreateInsert(rtEntity));
            }

            // If there were errors during navigation property resolution, we do not create any entities.
            if (mappingResult.Errors.Any())
            {
                throw NavigationPropertyException.MatchFailed(mappingResult);
            }

            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(sessionAccessor.Session, entityUpdateInfos,
                associationUpdateInfoList, operationResult);
            ResolveConnectionContextExtensions.ValidateOperationResult(operationResult);

            return await GetRtQueryRowResultSet(sessionAccessor.Session, ckCacheService, tenantRepository,
                entityUpdateInfos, queryRtId);
        }
        catch (Exception e)
        {
            return context.HandleException(e);
        }
    }


    private async Task<object?> ResolveDelete(IResolveFieldContext<object?> context)
    {
        try
        {
            var tenantContext = Helpers.GetTenantContext(context.UserContext);
            var tenantRepository = tenantContext.GetTenantRepository();

            var sessionAccessor = context.GetSessionAccessor();

            var inputObjects = context.GetArgument<List<RtEntityIdDto>>(Statics.EntitiesArg);

            var entityUpdateInfos = new List<EntityUpdateInfo<RtEntity>>();
            foreach (var rtEntityId in inputObjects)
            {
                entityUpdateInfos.Add(
                    EntityUpdateInfo<RtEntity>.CreateDelete(new RtEntityId(rtEntityId.CkTypeId, rtEntityId.RtId)));
            }

            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(sessionAccessor.Session, entityUpdateInfos, operationResult);
            ResolveConnectionContextExtensions.ValidateOperationResult(operationResult);

            return true;
        }
        catch (Exception e)
        {
            return context.HandleException(e);
        }
    }

    private async Task<object?> ResolveUpdate(IResolveFieldContext<object?> context)
    {
        try
        {
            var ckCacheService = context.GetCkCacheService();
            var sessionAccessor = context.GetSessionAccessor();

            if (context.Parent == null)
            {
                throw AssetRepositoryException.ParentUnavailable();
            }

            var tenantContext = Helpers.GetTenantContext(context.UserContext);
            var tenantRepository = tenantContext.GetTenantRepository();
            var graphQlUserContext = (GraphQlUserContext)context.UserContext;

            var queryRtId = context.Parent.GetArgument<OctoObjectId>(Statics.RtIdArg);
            var inputObjects = context.GetArgument<List<MutationDto<RtSimpleQueryRowDto>>>(Statics.EntitiesArg);

            var rtQuery = await tenantRepository.GetRtEntityByRtIdAsync<RtSimpleRtQuery>(sessionAccessor.Session, queryRtId);
            if (rtQuery == null)
            {
                throw AssetRepositoryException.QueryNotFound(queryRtId);
            }

            var mappingResult = new MappingResult();

            QueryMapperEngine queryMapperEngine = new QueryMapperEngine();
            var queryMapper = await queryMapperEngine.CreateQueryMapperAsync(ckCacheService, graphQlUserContext,
                rtQuery, tenantRepository, inputObjects.Select(i => i.Item).ToList(), sessionAccessor);


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

                await queryMapper.MapAsync(document, mutationDto.Item, associationUpdateInfoList, MappingMode.Update,
                    mappingResult);

                if (document.Attributes.Any())
                {
                    entityUpdateInfos.Add(EntityUpdateInfo<RtEntity>.CreateUpdate(rtEntityId, document));
                }
            }

            // If there were errors during navigation property resolution, we do not update any entities.
            if (mappingResult.Errors.Any())
            {
                throw NavigationPropertyException.MatchFailed(mappingResult);
            }

            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(sessionAccessor.Session, entityUpdateInfos,
                associationUpdateInfoList,
                operationResult);
            ResolveConnectionContextExtensions.ValidateOperationResult(operationResult);

            return await GetRtQueryRowResultSet(sessionAccessor.Session, ckCacheService, tenantRepository,
                entityUpdateInfos, queryRtId);
        }
        catch (Exception e)
        {
            return context.HandleException(e);
        }
    }
}