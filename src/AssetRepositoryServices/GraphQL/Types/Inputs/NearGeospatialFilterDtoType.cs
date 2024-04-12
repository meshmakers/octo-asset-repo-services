
using GraphQL.Types;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

internal sealed class NearGeospatialFilterDtoType : InputObjectGraphType<NearGeospatialFilterDto>
{
    public NearGeospatialFilterDtoType()
    {
        Name = "NearGeospatialFilter";
        Field(x => x.AttributeName);
        Field(x => x.Point, type: typeof(NonNullGraphType<PointInputGraphType>));
        Field(x => x.MinDistance, nullable:true);
        Field(x => x.MaxDistance, nullable:true);
    }
}