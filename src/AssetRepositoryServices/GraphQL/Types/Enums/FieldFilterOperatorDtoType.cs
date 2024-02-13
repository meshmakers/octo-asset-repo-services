using GraphQL.Types;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;

internal class FieldFilterOperatorDtoType : EnumerationGraphType<FieldFilterOperatorDto>
{
    public FieldFilterOperatorDtoType()
    {
        Name = "FieldFilterOperators";
        Description = "Defines the operator of field compare";
    }
}