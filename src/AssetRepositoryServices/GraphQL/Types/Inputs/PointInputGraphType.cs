using GraphQL.Types;
using Meshmakers.Octo.Runtime.Contracts.Geospatial.Geometry;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

internal sealed class PointInputGraphType : InputObjectGraphType<Point>
{
    public PointInputGraphType()
    {
        Field(x => x.Coordinates, type:typeof(NonNullGraphType<PositionInputGraphType>));
    }
}