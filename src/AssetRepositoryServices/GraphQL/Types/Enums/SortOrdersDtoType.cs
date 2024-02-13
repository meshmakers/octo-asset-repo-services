using GraphQL.Types;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;

internal class SortOrdersDtoType : EnumerationGraphType<SortOrdersDto>
{
    public SortOrdersDtoType()
    {
        Name = "SortOrders";
        Description = "Defines the sort order";
    }
}