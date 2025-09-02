using GraphQL.Types;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

internal sealed class RtGeospatialValueDtoType : ObjectGraphType<RtGeospatialValueDto>
{
    public RtGeospatialValueDtoType()
    {
        Field(d => d.Distance, true);
        Field(d => d.Point, typeof(PointGraphType));
    }
}