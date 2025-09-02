using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

internal sealed class RtEntityAttributeDtoType : ObjectGraphType<RtEntityAttributeDto>
{
    public RtEntityAttributeDtoType()
    {
        Name = "RtEntityAttribute";
        Description = "Attribute of a runtime entity";

        Field(x => x.AttributeName, typeof(StringGraphType)).Description("Attribute name within the entity.");
        Field<SimpleScalarType, object>(nameof(RtEntityAttributeDto.Value)).Description("Value of a scalar attribute.");
    }
}