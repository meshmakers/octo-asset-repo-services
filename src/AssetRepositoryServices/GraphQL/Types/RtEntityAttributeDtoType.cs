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
        Field<SimpleScalarType, object>(nameof(RtEntityAttributeDto.Value))
            .Description("Value of a scalar attribute. Always null for a Secret attribute, see 'secretIsSet'.");
        Field(x => x.SecretIsSet, typeof(BooleanGraphType))
            .Description("For a Secret attribute: true when the secret holds a value (the value itself is never " +
                         "returned). Null for every other attribute.");
        Field(x => x.SecretKeyMissing, typeof(BooleanGraphType))
            .Description("For a Secret attribute: true when a value is stored but cannot be read because its key " +
                         "id is not in the key ring (re-entry needed). Null for every other attribute.");
        Field(x => x.SecretSetAt, typeof(UtcDateTimeGraphType))
            .Description("For a Secret attribute: when the current value was set (UTC); null when not set, for " +
                         "legacy values and for every other attribute.");
    }
}