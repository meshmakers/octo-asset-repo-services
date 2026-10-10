using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;

/// <summary>
///     Test classes that CREATE or MODIFY runtime entities. They get their own fixture instance so their writes cannot skew the count-based assertions of the read-only classes in <see cref="GraphQlCollection" /> (AB#4963).
/// </summary>
[CollectionDefinition(Name)]
public class GraphQlMutatingCollection : ICollectionFixture<GraphQlTestFixture>
{
    public const string Name = "GraphQlMutating";
}

/// <summary>
///     AB#6385: GraphQL against a tenant whose data policy opts <c>Customer</c> into the blueprint-lock protection.
/// </summary>
[CollectionDefinition(Name)]
public class BlueprintLockCollection : ICollectionFixture<BlueprintLockGraphQlTestFixture>
{
    public const string Name = "BlueprintLock";
}
