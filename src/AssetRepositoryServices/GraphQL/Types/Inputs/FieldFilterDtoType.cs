using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

internal sealed class FieldFilterDtoType : InputObjectGraphType<FieldFilterDto>
{
    public FieldFilterDtoType()
    {
        Name = "FieldFilter";
        Field(x => x.AttributePath);
        Field(x => x.Operator, typeof(NonNullGraphType<FieldFilterOperatorDtoType>));
        Field(x => x.ComparisonValue, typeof(SimpleScalarType));
        Field(x => x.SecondaryValue, typeof(SimpleScalarType))
            .Description("Secondary value for two-argument operators such as Between.");
    }
}