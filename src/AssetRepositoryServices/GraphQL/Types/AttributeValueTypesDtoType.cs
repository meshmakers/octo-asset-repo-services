using GraphQL.Types;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;

#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

public class AttributeValueTypesDtoType : EnumerationGraphType<AttributeValueTypesDto>
{
    public AttributeValueTypesDtoType()
    {
        Name = "AttributeValueType";
        Description = "Enum of valid attribute types";
    }
}
