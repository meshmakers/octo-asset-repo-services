using Meshmakers.Octo.Runtime.Contracts.Repositories;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

internal class NavigationPairMappingError : MappingError
{
    public required NavigationPair NavigationPair { get; init; }

    public required IList<RtEntityGraphItem> Candidates { get; set; }
}