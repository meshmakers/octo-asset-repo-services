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
