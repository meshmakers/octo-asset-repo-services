using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;

/// <summary>
///     Shares one <see cref="BlueprintTestFixture" /> - and therefore one MongoDB container plus the blueprint test tenant - across every test
///     class that joins this collection, replacing the per-class
///     <c>IClassFixture&lt;BlueprintTestFixture&gt;</c> (AB#4963).
/// </summary>
[CollectionDefinition(Name)]
public class BlueprintCollection : ICollectionFixture<BlueprintTestFixture>
{
    public const string Name = "Blueprint";
}
