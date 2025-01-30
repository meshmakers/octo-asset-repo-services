using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

internal sealed class CkTypeQueryColumnDtoType : ObjectGraphType<CkTypeQueryColumnDto>
{
    public CkTypeQueryColumnDtoType()
    {
        Name = "CkTypeQueryColumn";
        Description = "Represents a possible column in a query result.";

        Field(x => x.AttributeName, type: typeof(NonNullGraphType<StringGraphType>))
            .Description("Attribute name within the entity.");
        Field(x => x.AttributeValueType, type: typeof(NonNullGraphType<AttributeValueTypesDtoType>))
            .Description("Value type of the attribute.");
    }
}