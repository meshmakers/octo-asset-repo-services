using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;

#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

public sealed class CkTypeAttributeDtoType : ObjectGraphType<CkTypeAttributeDto>
{
    public CkTypeAttributeDtoType()
    {
        Name = "CkTypeAttribute";
        Description = "Attributes of a construction kit type";

        Field(x => x.CkAttributeId, type: typeof(NonNullGraphType<CkIdType<CkAttributeId>>)).Description("Octo Identifier of the attribute.");
        Field(x => x.AttributeName, type: typeof(StringGraphType)).Description("Attribute name within the entity.");
        Field(x => x.AttributeValueType, type: typeof(AttributeValueTypesDtoType))
            .Description("Attribute name within the type.");
        Field(x => x.AutoCompleteValues, type: typeof(ListGraphType<StringGraphType>))
            .Description("Auto complete values for the attribute.");
        Field(x => x.AutoIncrementReference, type: typeof(StringGraphType))
            .Description("Auto increment reference for the attribute.");
        Field(x => x.Attribute, type: typeof(CkAttributeDtoType))
            .Description("The construction kit attribute definition");
    }
}