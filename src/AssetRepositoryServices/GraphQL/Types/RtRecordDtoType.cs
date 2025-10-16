using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration.DependencyInjection.Options;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     Implements the GraphQL runtime record type
/// </summary>
[DoNotRegister]
internal sealed class RtRecordDtoType : ObjectGraphType<RtRecordDto>
{
    /// <inheritdoc />
    public RtRecordDtoType(RtCkId<CkRecordId> ckRecordId)
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
    public RtCkId<CkRecordId> CkRecordId { get; }


    internal void Populate(IOptions<OctoAssetRepositoryServicesOptions> options, IGraphTypesCache graphTypesCache,
        CkRecordGraph ckRecordGraph)
    {
        AddConstructionKit(ckRecordGraph);

        var builder = OctoBuilder<RtRecordDto>.Create(this, options);
        foreach (var attribute in ckRecordGraph.AllAttributes.Values)
        {
            builder.Attribute(graphTypesCache, attribute, false);
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
        var ckTypeGraph = (CkTypeGraph)arg.FieldDefinition.Metadata[Statics.TypeGraphType]!;
        return CkTypeDtoType.CreateCkTypeDto(ckTypeGraph);
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

    internal static RtRecordDto CreateRtRecordDtoWithAttributes(ICkCacheService ckCacheService, string tenantId,
        RtRecord rtRecord, bool resolveEnumValuesToNames, ICollection<string>? filterAttributeNames = null)
    {
        var rtRecordDto = new RtRecordDto
        {
            CkRecordId = rtRecord.CkRecordId,
            UserContext = rtRecord
        };

        var ckRecordGraph = ckCacheService.GetRtCkRecord(tenantId, rtRecord.CkRecordId);

        IEnumerable<CkTypeAttributeGraph> resultList;
        if (filterAttributeNames != null && filterAttributeNames.Any())
        {
            resultList =
                ckRecordGraph.AllAttributes.Values.Where(a =>
                    filterAttributeNames.Contains(a.AttributeName.ToCamelCase()));
        }
        else
        {
            resultList = ckRecordGraph.AllAttributes.Values;
        }

        var attributeDtos =
            resultList.Select(item =>
                RtEntityGenericDtoType.CreateRtEntityAttributeDto(ckCacheService, tenantId, rtRecord, item,
                    resolveEnumValuesToNames, filterAttributeNames));
        rtRecordDto.Attributes = attributeDtos.ToList();
        return rtRecordDto;
    }
}