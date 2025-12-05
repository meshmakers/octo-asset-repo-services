using GraphQL.Types;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;

internal class MultiplicitiesDtoType : EnumerationGraphType<MultiplicitiesDto>
{
    public MultiplicitiesDtoType()
    {
        Name = "Multiplicities";
        Description = "Enum of valid multiplicities for association roles";
    }
}