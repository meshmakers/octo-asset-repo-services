using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;

/// <summary>
///     Shares one <see cref="GraphQlTestFixture" /> - and therefore one MongoDB container plus the seeded sample data and the GraphQL schema - across every test
///     class that joins this collection, replacing the per-class
///     <c>IClassFixture&lt;GraphQlTestFixture&gt;</c> (AB#4963).
/// </summary>
[CollectionDefinition(Name)]
public class GraphQlCollection : ICollectionFixture<GraphQlTestFixture>
{
    public const string Name = "GraphQl";
}
