using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;

/// <summary>
///     Shares one <see cref="SampleDataFixture" /> - and therefore one MongoDB container plus the seeded sample data - across every test
///     class that joins this collection, replacing the per-class
///     <c>IClassFixture&lt;SampleDataFixture&gt;</c> (AB#4963).
/// </summary>
[CollectionDefinition(Name)]
public class SampleDataCollection : ICollectionFixture<SampleDataFixture>
{
    public const string Name = "SampleData";
}
