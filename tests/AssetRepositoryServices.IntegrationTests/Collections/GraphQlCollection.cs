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

/// <summary>
///     F1.5-S4 (AB#5923): GraphQL executed by a host in the Production environment.
/// </summary>
[CollectionDefinition(Name)]
public class GraphQlProductionCollection : ICollectionFixture<ProductionGraphQlTestFixture>
{
    public const string Name = "GraphQlProduction";
}

/// <summary>
///     F1.5-S4 (AB#5923): GraphQL executed by a host in the Development environment.
/// </summary>
[CollectionDefinition(Name)]
public class GraphQlDevelopmentCollection : ICollectionFixture<DevelopmentGraphQlTestFixture>
{
    public const string Name = "GraphQlDevelopment";
}
