using GraphQL;
using GraphQL.DataLoader;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     Implements the GraphQL runtime record type
/// </summary>
[DoNotRegister]
internal sealed class RtRecordDtoType : ObjectGraphType<RtRecordDto>
{
    /// <inheritdoc />
    public RtRecordDtoType(CkId<CkRecordId> ckRecordId)
    {
        CkRecordId = ckRecordId;

        Name = ckRecordId.GetGraphQlPascalCaseName();
        Description = $"Runtime entities of construction kit record '{ckRecordId}'";
        IsTypeOf = o =>
        {
            if (o is RtRecordDto rtRecordDto)
            {
                return rtRecordDto.CkRecordId == ckRecordId;
            }

            return false;
        };
    }

    /// <summary>
    ///     Returns the Construction Kid Id of the object type
    /// </summary>
    public CkId<CkRecordId> CkRecordId { get; }


    internal void Populate(ICkCacheService ckCacheService, string tenantId, IGraphTypesCache graphTypesCache, CkRecordGraph entityCacheItem)
    {
        AddConstructionKit(entityCacheItem);

        foreach (var attribute in entityCacheItem.AllAttributes.Values)
        {
            Helpers.AddAttribute(this, graphTypesCache, attribute, false);
        }
    }

    private void AddConstructionKit(CkRecordGraph ckTypeGraph)
    {
        Field<CkTypeDtoType>("ConstructionKitType")
            .Metadata(Statics.TypeGraphType, ckTypeGraph)
            .Resolve(ResolveCkEntity);
    }

    private object ResolveCkEntity(IResolveFieldContext<RtRecordDto> arg)
    {
        // TODO: Fix save cast to CkTypeGraph
        var entityCacheItem = (CkTypeGraph)arg.FieldDefinition.Metadata[Statics.TypeGraphType]!;
        return CkTypeDtoType.CreateCkTypeDto(entityCacheItem);
    }

    internal static RtRecordDto CreateRtRecordDto(RtRecord rtEntity)
    {
        var rtRecordDto = new RtRecordDto
        {
            CkRecordId = rtEntity.CkRecordId,
            UserContext = rtEntity
        };
        return rtRecordDto;
    }
}