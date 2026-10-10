using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;

/// <summary>
///     One <see cref="BlueprintUpdateFlowFixture" /> for the blueprint update flow tests (AB#6315).
/// </summary>
[CollectionDefinition(Name)]
public class BlueprintUpdateFlowCollection : ICollectionFixture<BlueprintUpdateFlowFixture>
{
    public const string Name = "BlueprintUpdateFlow";
}
