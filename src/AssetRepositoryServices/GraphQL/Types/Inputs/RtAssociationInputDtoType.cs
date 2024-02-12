using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class RtAssociationInputDtoType : InputObjectGraphType<RtAssociationInputDto>
{
    public RtAssociationInputDtoType()
    {
        Name = "RtAssociationInput";
        Description = "Input field for associations";

        Field(x => x.Target, type: typeof(NonNullGraphType<RtEntityIdType>))
            .Description("Runtime ID of the target entity");
        Field(x => x.ModOption, type: typeof(AssociationModOptionsDtoType)).Description("Type of modification.");
    }
}