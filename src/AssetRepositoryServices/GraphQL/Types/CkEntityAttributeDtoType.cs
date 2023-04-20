using GraphQL.Types;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;

#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

public class CkEntityAttributeDtoType : ObjectGraphType<CkEntityAttributeDto>
{
    public CkEntityAttributeDtoType()
    {
        Name = "CkEntityAttribute";
        Description = "Attributes of a construction kit entity";

        Field(x => x.AttributeId, type: typeof(IdGraphType)).Description("Octo Identifier of the attribute.");
        Field(x => x.AttributeName, type: typeof(StringGraphType)).Description("Attribute name within the entity.");
        Field(x => x.AttributeValueType, type: typeof(AttributeValueTypesDtoType))
            .Description("Attribute name within the entity.");
        Field(x => x.IsAutoCompleteEnabled, type: typeof(BooleanGraphType))
            .Description("Returns true, when auto complete values are enabled.");
        Field(x => x.AutoCompleteTexts, type: typeof(ListGraphType<StringGraphType>))
            .Description("Auto complete values for the attribute.");
        Field(x => x.AutoCompleteLimit, type: typeof(IntGraphType))
            .Description("Auto complete max value count for the attribute.");
        Field(x => x.AutoCompleteFilter, type: typeof(StringGraphType))
            .Description("Auto complete filter value for the attribute.");
        Field(x => x.AutoIncrementReference, type: typeof(StringGraphType))
            .Description("Auto increment reference for the attribute.");
        Field(x => x.Attribute, type: typeof(CkAttributeDtoType))
            .Description("The construction kit attribute definition");
    }
}
