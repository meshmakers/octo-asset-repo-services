using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

internal abstract class RtMutationBase : ObjectGraphType
{
    protected void RtEntityFromInputObject(ICkCacheService ckCacheService, string tenantId, RtEntity rtEntity,
        RtEntityDto rtEntityDto,
        List<AssociationUpdateInfo> associations)
    {
        var ckTypeGraph =
            ckCacheService.GetCkType(tenantId, rtEntity.CkTypeId ?? throw OctoGraphQLException.CkTypeIdUndefined());

        rtEntity.RtWellKnownName = rtEntityDto.RtWellKnownName;

        if (rtEntityDto.Properties != null)
        {
            foreach (var item in rtEntityDto.Properties)
            {
                if (TryHandleAttribute(rtEntity, ckTypeGraph, item))
                {
                    continue;
                }

                if (TryHandleInboundAssoc(rtEntity, ckTypeGraph, item, associations))
                {
                    continue;
                }

                TryHandleOutboundAssoc(rtEntity, ckTypeGraph, item, associations);
            }
        }
    }

    protected async Task<IEnumerable<RtEntityDto>> GetResultSet(IOctoSession session, ITenantRepository repository,
        CkId<CkTypeId> ckTypeId,
        List<EntityUpdateInfo<RtEntity>> entityUpdateInfos)
    {
        var resultSet = await repository.GetRtEntitiesByIdAsync(session, ckTypeId,
            entityUpdateInfos.Select(x => x.RtEntityId.RtId).ToList(), DataQueryOperation.Create());

        return resultSet.Items.Select(RtEntityDtoType.CreateRtEntityDto);
    }


    private bool TryHandleAttribute(RtEntity rtEntity, CkTypeGraph entityCacheItem,
        KeyValuePair<string, object?> item)
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
        KeyValuePair<string, object?> item, List<AssociationUpdateInfo> associations)
    {
        var assocName = item.Key;

        var ckTypeAssociationGraph = entityCacheItem.Associations.In.All
            .FirstOrDefault(a => a.NavigationPropertyName == assocName);
        if (ckTypeAssociationGraph == null)
        {
            return false;
        }

        if (item.Value != null)
        {
            var rtAssociationInputDtos = (IEnumerable<object>)item.Value;
            foreach (RtAssociationInputDto rtAssociationDto in rtAssociationInputDtos)
            {
                var assocInfo = new AssociationUpdateInfo(
                    new RtEntityId(rtAssociationDto.Target.CkTypeId, rtAssociationDto.Target.RtId),
                    rtEntity.ToRtEntityId(),
                    ckTypeAssociationGraph.CkRoleId,
                    rtAssociationDto.ModOption ?? AssociationModOptionsDto.Create);
                associations.Add(assocInfo);
            }
        }

        return true;
    }

    // ReSharper disable once UnusedMethodReturnValue.Local
    private bool TryHandleOutboundAssoc(RtEntity rtEntity, CkTypeGraph entityCacheItem,
        KeyValuePair<string, object?> item, List<AssociationUpdateInfo> associations)
    {
        var assocName = item.Key;

        var typeAssociationGraph = entityCacheItem.Associations.Out.All
            .FirstOrDefault(a => a.NavigationPropertyName == assocName);
        if (typeAssociationGraph == null)
        {
            return false;
        }

        if (item.Value != null)
        {
            var rtAssociationInputDtos = (IEnumerable<object>)item.Value;
            foreach (RtAssociationInputDto rtAssociationDto in rtAssociationInputDtos)
            {
                var assocInfo = new AssociationUpdateInfo(
                    rtEntity.ToRtEntityId(),
                    new RtEntityId(rtAssociationDto.Target.CkTypeId, rtAssociationDto.Target.RtId),
                    typeAssociationGraph.CkRoleId,
                    rtAssociationDto.ModOption ?? AssociationModOptionsDto.Create);
                associations.Add(assocInfo);
            }
        }

        return true;
    }
}