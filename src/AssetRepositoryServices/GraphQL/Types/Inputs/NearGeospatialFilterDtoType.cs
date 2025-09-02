using GraphQL.Types;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

internal sealed class NearGeospatialFilterDtoType : InputObjectGraphType<NearGeospatialFilterDto>
{
    public NearGeospatialFilterDtoType()
    {
        Name = "NearGeospatialFilter";
        Field(x => x.AttributeName);
        Field(x => x.Point, typeof(NonNullGraphType<PointInputGraphType>));
        Field(x => x.MinDistance, true);
        Field(x => x.MaxDistance, true);
    }
}