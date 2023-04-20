using System.Linq;
using GraphQL.Types;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;
using Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     Construction kit attributes Graph QL type definition
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class CkAttributeDtoType : ObjectGraphType<CkAttributeDto>
{
    /// <inheritdoc />
    public CkAttributeDtoType()
    {
        Name = "CkAttribute";
        Description = "Construction kit attribute definitions";

        Field(x => x.AttributeId, type: typeof(IdGraphType)).Description("Unique id of the object.");
        Field(x => x.ScopeId, type: typeof(ScopeIdsDtoType));
        Field(x => x.AttributeValueType, type: typeof(AttributeValueTypesDtoType));
        Field(x => x.SelectionValues, type: typeof(ListGraphType<CkSelectionValueDtoType>))
            .Description("Selection values for the attribute.");

        Field<SimpleScalarType, object>(nameof(CkAttributeDto.DefaultValue))
            .Description("Default value of a scalar attribute.");
        Field<ListGraphType<SimpleScalarType>, object>(nameof(CkAttributeDto.DefaultValues))
            .Description("Default values of a compound attribute.");
    }

    internal static CkAttributeDto CreateCkAttributeDto(AttributeCacheItem attributeCacheItem)
    {
        var attributeDto = new CkAttributeDto
        {
            AttributeId = attributeCacheItem.AttributeId,
            ScopeId = (ScopeIdsDto)attributeCacheItem.ScopeId,
            AttributeValueType = (AttributeValueTypesDto)attributeCacheItem.AttributeValueType,
            DefaultValue = attributeCacheItem.DefaultValue,
            DefaultValues = attributeCacheItem.DefaultValues,
            SelectionValues = attributeCacheItem.SelectionValues
                ?.Select(sv => new CkSelectionValueDto { Key = sv.Key, Name = sv.Name }).ToList()
        };

        return attributeDto;
    }

    internal static CkAttributeDto CreateCkAttributeDto(CkAttribute ckAttribute)
    {
        var attributeDto = new CkAttributeDto
        {
            AttributeId = ckAttribute.AttributeId,
            ScopeId = (ScopeIdsDto)ckAttribute.ScopeId,
            AttributeValueType = (AttributeValueTypesDto)ckAttribute.AttributeValueType,
            DefaultValue = ckAttribute.DefaultValue,
            DefaultValues = ckAttribute.DefaultValues,
            SelectionValues = ckAttribute.SelectionValues
                ?.Select(sv => new CkSelectionValueDto { Key = sv.Key, Name = sv.Name }).ToList()
        };

        return attributeDto;
    }
}
