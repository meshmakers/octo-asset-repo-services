using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;

/// <summary>
///     Recompute tests that run with the immediate sweep (AB#6968) and the production late-data debounce (AB#9016, AB#9099).
/// </summary>
[CollectionDefinition(Name)]
public class StreamDataDefaultDebounceCollection : ICollectionFixture<DefaultDebounceStreamDataFixture>
{
    public const string Name = "StreamDataDefaultDebounce";
}
