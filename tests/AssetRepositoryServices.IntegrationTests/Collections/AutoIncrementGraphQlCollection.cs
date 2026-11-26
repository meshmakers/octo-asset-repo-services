using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;

/// <summary>
///     One <see cref="AutoIncrementGraphQlTestFixture" /> for the transient transaction retry of GraphQL mutations.
/// </summary>
[CollectionDefinition(Name)]
public class AutoIncrementGraphQlCollection : ICollectionFixture<AutoIncrementGraphQlTestFixture>
{
    public const string Name = "AutoIncrementGraphQl";
}
