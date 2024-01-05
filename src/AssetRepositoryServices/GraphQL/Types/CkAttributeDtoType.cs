using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository.Entities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     Construction kit attributes Graph QL type definition
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public sealed class CkAttributeDtoType : ObjectGraphType<CkAttributeDto>
{
    /// <inheritdoc />
    public CkAttributeDtoType()
    {
        Name = "CkAttribute";
        Description = "Construction kit attribute definitions";

        Field(x => x.CkAttributeId, type: typeof(NonNullGraphType<CkIdType<CkAttributeId>>)).Description("Unique id of the object.");
        Field(x => x.AttributeValueType, type: typeof(NonNullGraphType<AttributeValueTypesDtoType>));
        Field(x => x.ValueCkRecordId, type: typeof(CkIdType<CkRecordId>)).Description("Optional record id of the attribute value type.");
        Field(x => x.ValueCkEnumId, type: typeof(CkIdType<CkEnumId>)).Description("Optional enum id of the attribute value type.");
        Field(x => x.Description, type: typeof(StringGraphType)).Description("Optional description of the attribute.");
        Field(x => x.IsDataStream, type: typeof(BooleanGraphType))
            .Description("Optional flag that tells if an attribute is a data stream.");
        Field<ListGraphType<SimpleScalarType>, object>(nameof(CkAttributeDto.DefaultValues))
            .Description("Default values of a compound attribute.");
    }

    internal static CkAttributeDto CreateCkAttributeDto(CkTypeAttributeGraph attributeCacheItem)
    {
        var attributeDto = new CkAttributeDto
        {
            CkAttributeId = attributeCacheItem.CkAttributeId,
            AttributeValueType = attributeCacheItem.ValueType,
            DefaultValues = attributeCacheItem.DefaultValues
        };

        return attributeDto;
    }

    internal static CkAttributeDto CreateCkAttributeDto(CkAttribute ckAttribute)
    {
        var attributeDto = new CkAttributeDto
        {
            CkAttributeId = ckAttribute.AttributeId,
            AttributeValueType = ckAttribute.AttributeValueType,
            DefaultValues = ckAttribute.DefaultValues
        };

        return attributeDto;
    }
}