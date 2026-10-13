using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;

/// <summary>
///     Test classes that need archive tables with a replica copy (AB#6647) and therefore run on a two-node CrateDB cluster of their own instead of the shared single node.
/// </summary>
[CollectionDefinition(Name)]
public class StreamDataReplicatedCollection : ICollectionFixture<ReplicatedStreamDataFixture>
{
    public const string Name = "StreamDataReplicated";
}
