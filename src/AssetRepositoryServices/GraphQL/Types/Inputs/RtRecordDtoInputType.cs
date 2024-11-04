using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

/// <summary>
///     Implements a GraphQL runtime entity type
/// </summary>
[DoNotRegister]
internal sealed class RtRecordDtoInputType : InputObjectGraphType<RtRecordDto>
{
    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="ckRecordId">Corresponding construction kit id</param>
    public RtRecordDtoInputType(CkId<CkRecordId> ckRecordId)
    {
        CkRecordId = ckRecordId;
        Name = $"{ckRecordId.GetGraphQlPascalCaseName()}{Statics.GraphQlInputSuffix}";
    }

    /// <summary>
    ///     Returns the construction kit id
    /// </summary>
    public CkId<CkRecordId> CkRecordId { get; }

    /// <inheritdoc />
    /// <remarks>We need an overload, to deserialize all properties to the dictionary of <see cref="RtEntityDto" /></remarks>
    public override object ParseDictionary(IDictionary<string, object?> value)
    {
        var rtRecordDto = value.ToObjectWithWithUnknownProperties<RtRecordDto>(out var unmappedDictionary);
        rtRecordDto.CkRecordId = CkRecordId;
        
        if (unmappedDictionary.Count > 0)
        {
            rtRecordDto.Attributes = new List<RtEntityAttributeDto>();
            foreach (var (dictKey, dictValue) in unmappedDictionary)
            {
                rtRecordDto.Attributes.Add(new RtEntityAttributeDto
                {
                    AttributeName = dictKey,
                    Value = dictValue
                });
            }
        }

        return rtRecordDto;
    }

    /// <summary>
    ///     Populates the type with ck related attributes and associations
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="graphTypesCache"></param>
    /// <param name="recordGraph">The cache item</param>
    /// <param name="ckCacheService"></param>
    public void Populate(ICkCacheService ckCacheService, string tenantId, IGraphTypesCache graphTypesCache, CkRecordGraph recordGraph)
    {
        foreach (var attribute in recordGraph.AllAttributes.Values)
        {
            Helpers.AddAttribute(this, graphTypesCache, attribute, true);
        }
    }
}