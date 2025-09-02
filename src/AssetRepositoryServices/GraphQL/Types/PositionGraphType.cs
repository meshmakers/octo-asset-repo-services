using GraphQL.Types;
using Meshmakers.Octo.Runtime.Contracts.Geospatial.Geometry;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

internal sealed class PositionGraphType : ObjectGraphType<Position>
{
    public PositionGraphType()
    {
        Field(x => x.Latitude);
        Field(x => x.Longitude);
        Field(x => x.Altitude, true);
    }
}