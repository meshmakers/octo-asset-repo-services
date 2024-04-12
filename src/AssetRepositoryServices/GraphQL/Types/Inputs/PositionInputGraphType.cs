using GraphQL.Types;
using Meshmakers.Octo.Runtime.Contracts.Geospatial.Geometry;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

internal sealed class PositionInputGraphType : InputObjectGraphType<Position>
{
    public PositionInputGraphType()
    {
        Field(x => x.Latitude);
        Field(x => x.Longitude);
    }
}