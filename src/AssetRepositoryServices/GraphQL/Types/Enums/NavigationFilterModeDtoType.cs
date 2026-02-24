using GraphQL.Types;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;

internal class NavigationFilterModeDtoType : EnumerationGraphType<NavigationFilterMode>
{
    public NavigationFilterModeDtoType()
    {
        Name = "NavigationFilterMode";
        Description =
            "Controls how navigation properties affect the result set. " +
            "FILTER (default): entities without associations are excluded. " +
            "INCLUDE: entities without associations are kept; navigation lookups run post-pagination for better performance.";
    }
}
