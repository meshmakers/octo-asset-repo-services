using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

internal sealed class RtEntityAttributeDtoInputType : InputObjectGraphType<RtEntityAttributeDto>
{
    public RtEntityAttributeDtoInputType()
    {
        Name = $"RtEntityAttribute{Statics.GraphQlInputSuffix}";
        Description = "Attribute of a runtime entity";

        Field(x => x.AttributeName, type: typeof(StringGraphType)).Description("Attribute name within the entity.");
        Field<SimpleScalarType, object>(nameof(RtEntityAttributeDto.Value)).Description("Value of a scalar attribute.");
    }
}