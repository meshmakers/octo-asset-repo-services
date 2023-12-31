using GraphQL.Types;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

public class FieldFilterOperatorDtoType : EnumerationGraphType<FieldFilterOperatorDto>
{
    public FieldFilterOperatorDtoType()
    {
        Name = "FieldFilterOperators";
        Description = "Defines the operator of field compare";
    }
}
