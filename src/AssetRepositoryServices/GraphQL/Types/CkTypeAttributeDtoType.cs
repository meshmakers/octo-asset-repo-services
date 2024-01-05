using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

internal sealed class CkTypeAttributeDtoType : ObjectGraphType<CkTypeAttributeDto>
{
    public CkTypeAttributeDtoType()
    {
        Name = "CkTypeAttribute";
        Description = "Attributes of a construction kit type";

        Field(x => x.CkAttributeId, type: typeof(NonNullGraphType<CkIdTypeGraph<CkAttributeId>>))
            .Description("Construction kit attribute id.");
        Field(x => x.AttributeName, type: typeof(NonNullGraphType<StringGraphType>))
            .Description("Attribute name within the entity.");
        Field(x => x.AttributeValueType, type: typeof(NonNullGraphType<AttributeValueTypesDtoType>))
            .Description("Value type of the attribute.");
        Field(x => x.AutoCompleteValues, type: typeof(ListGraphType<StringGraphType>))
            .Description("Auto complete values for the attribute.");
        Field(x => x.AutoIncrementReference, type: typeof(StringGraphType))
            .Description("Auto increment reference for the attribute.");
        Field(x=> x.IsOptional)
            .Description("Defines if the attribute is optional.");
        Field(x => x.Attribute, type: typeof(CkAttributeDtoType))
            .Description("The construction kit attribute definition");
    }
}