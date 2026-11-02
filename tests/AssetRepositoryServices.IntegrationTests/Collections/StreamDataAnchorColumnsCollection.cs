using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;

/// <summary>
///     Test classes of the anchor columns of stream-data queries (AB#7812): archives whose rows point at real entities and
///     a data policy on one of the anchor types.
/// </summary>
[CollectionDefinition(Name)]
public class StreamDataAnchorColumnsCollection : ICollectionFixture<AnchorColumnsStreamDataFixture>
{
    public const string Name = "StreamDataAnchorColumns";
}
