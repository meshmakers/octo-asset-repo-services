using GraphQL.Types;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

internal sealed class PositionInputGraphType : InputObjectGraphType<PositionDto>
{
    public PositionInputGraphType()
    {
        Field(x => x.Latitude);
        Field(x => x.Longitude);
    }
}