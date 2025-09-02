using GraphQL.Types;
using Meshmakers.Octo.Runtime.Contracts.Geospatial.Geometry;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

internal sealed class PointGraphType : ObjectGraphType<Point>
{
    public PointGraphType()
    {
        Field(x => x.Coordinates, typeof(PositionGraphType));
    }
}