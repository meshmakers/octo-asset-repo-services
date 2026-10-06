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

        Field(x => x.AttributeName, typeof(StringGraphType)).Description("Attribute name within the entity.");
        Field<SimpleScalarType, object>(nameof(RtEntityAttributeDto.Value))
            .Description("Value of a scalar attribute. For a Secret attribute a non-empty string sets the " +
                         "secret; null, an empty string or an omitted value keep it unchanged.");
        Field(x => x.SecretIsSet, typeof(BooleanGraphType))
            .Description("Accepted and ignored, so a client can send back what it read. Clearing a secret " +
                         "is only possible with 'clearSecretAttributes'.");
    }
}