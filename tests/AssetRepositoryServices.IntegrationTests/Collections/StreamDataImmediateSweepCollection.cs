using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;

/// <summary>
///     Recompute tests that assert the immediate sweep of the superseded generation (sweep grace period zero, AB#6968).
/// </summary>
[CollectionDefinition(Name)]
public class StreamDataImmediateSweepCollection : ICollectionFixture<ImmediateSweepStreamDataFixture>
{
    public const string Name = "StreamDataImmediateSweep";
}
