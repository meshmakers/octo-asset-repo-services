using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration.DependencyInjection.Options;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Microsoft.Extensions.Options;

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
    /// <param name="options"></param>
    /// <param name="graphTypesCache"></param>
    /// <param name="recordGraph">The cache item</param>
    public void Populate(IOptions<OctoAssetRepositoryServicesOptions> options, IGraphTypesCache graphTypesCache,
        CkRecordGraph recordGraph)
    {
        var builder = OctoBuilder<RtRecordDto>.Create(this, options);
        foreach (var attribute in recordGraph.AllAttributes.Values)
        {
            builder.Attribute(graphTypesCache, attribute, true);
        }
    }
}