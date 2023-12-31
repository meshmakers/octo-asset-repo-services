using GraphQL.Types;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     Represents a runtime entity attribute type
/// </summary>
public sealed class RtEntityAttributeDtoType : ObjectGraphType<RtEntityAttributeDto>
{
    /// <summary>
    ///     C
    /// </summary>
    public RtEntityAttributeDtoType()
    {
        Name = "RtEntityAttribute";
        Description = "Attribute of a runtime entity";

        Field(x => x.AttributeName, type: typeof(StringGraphType)).Description("Attribute name within the entity.");

        Field<SimpleScalarType, object>(nameof(RtEntityAttributeDto.Value)).Description("Value of a scalar attribute.");
        Field<ListGraphType<SimpleScalarType>, object>(nameof(RtEntityAttributeDto.Values))
            .Description("Values of a compound attribute.");
    }
}
