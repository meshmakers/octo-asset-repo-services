using GraphQL.Types;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;

#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

public sealed class FieldFilterDtoType : InputObjectGraphType<FieldFilterDto>
{
    public FieldFilterDtoType()
    {
        Name = "FieldFilter";
        Field(x => x.AttributeName);
        Field(x => x.Operator, type: typeof(FieldFilterOperatorDtoType));
        Field(x => x.ComparisonValue, type: typeof(SimpleScalarType));
    }
}
