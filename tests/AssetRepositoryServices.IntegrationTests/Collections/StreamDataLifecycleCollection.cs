using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;

/// <summary>
///     Test classes that change tenant-level stream-data state - disabling the capability, dropping child tenants and their archive tables. Isolated because that state is shared by every other stream-data class (AB#4963).
/// </summary>
[CollectionDefinition(Name)]
public class StreamDataLifecycleCollection : ICollectionFixture<StreamDataFixture>
{
    public const string Name = "StreamDataLifecycle";
}
