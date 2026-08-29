using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;

/// <summary>
///     Shares one <see cref="AssetRepoFixture" /> - and therefore one MongoDB container plus the provisioned test tenant - across every test
///     class that joins this collection, replacing the per-class
///     <c>IClassFixture&lt;AssetRepoFixture&gt;</c> (AB#4963).
/// </summary>
[CollectionDefinition(Name)]
public class AssetRepoCollection : ICollectionFixture<AssetRepoFixture>
{
    public const string Name = "AssetRepo";
}
