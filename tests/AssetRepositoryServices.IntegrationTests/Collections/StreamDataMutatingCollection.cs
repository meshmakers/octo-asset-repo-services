using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;

/// <summary>
///     Test classes that INSERT stream-data rows or add computed columns. Isolated from <see cref="StreamDataCollection" /> so the read-only query tests keep counting only the fixture's own seed (AB#4963).
/// </summary>
[CollectionDefinition(Name)]
public class StreamDataMutatingCollection : ICollectionFixture<StreamDataFixture>
{
    public const string Name = "StreamDataMutating";
}
