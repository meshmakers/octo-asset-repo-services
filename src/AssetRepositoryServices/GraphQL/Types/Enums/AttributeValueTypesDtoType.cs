using GraphQL.Types;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;

internal class AttributeValueTypesDtoType : EnumerationGraphType<AttributeValueTypesDto>
{
    public AttributeValueTypesDtoType()
    {
        Name = "AttributeValueType";
        Description = "Enum of valid attribute types";
    }
}