using GraphQL.Types;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

internal sealed class PointInputGraphType : InputObjectGraphType<PointDto>
{
    public PointInputGraphType()
    {
        Field(x => x.Coordinates, typeof(NonNullGraphType<PositionInputGraphType>));
    }
}