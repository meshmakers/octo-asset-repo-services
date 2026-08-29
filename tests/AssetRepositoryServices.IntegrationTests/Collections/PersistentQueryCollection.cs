using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;

/// <summary>
///     Test classes that both CREATE entities and assert on unscoped totals ("expected 8 metering
///     points"). They cannot share a fixture with any other writer, so they get one of their own
///     (AB#4963).
/// </summary>
[CollectionDefinition(Name)]
public class PersistentQueryCollection : ICollectionFixture<GraphQlTestFixture>
{
    public const string Name = "PersistentQuery";
}
