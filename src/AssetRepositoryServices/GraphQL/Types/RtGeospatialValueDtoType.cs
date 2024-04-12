using GraphQL.Types;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

internal sealed class RtGeospatialValueDtoType : ObjectGraphType<RtGeospatialValueDto>
{
    public RtGeospatialValueDtoType()
    {
        Field(d => d.Distance, nullable: true);
        Field(d => d.Point, type: typeof(PointGraphType));
    }
}