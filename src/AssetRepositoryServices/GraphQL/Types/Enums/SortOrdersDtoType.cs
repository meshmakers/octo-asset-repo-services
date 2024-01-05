using GraphQL.Types;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;

public class SortOrdersDtoType : EnumerationGraphType<SortOrdersDto>
{
    public SortOrdersDtoType()
    {
        Name = "SortOrders";
        Description = "Defines the sort order";
    }
}