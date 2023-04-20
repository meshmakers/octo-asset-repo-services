using GraphQL.Types;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;

#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

public class SortOrdersDtoType : EnumerationGraphType<SortOrdersDto>
{
    public SortOrdersDtoType()
    {
        Name = "SortOrders";
        Description = "Defines the sort order";
    }
}
