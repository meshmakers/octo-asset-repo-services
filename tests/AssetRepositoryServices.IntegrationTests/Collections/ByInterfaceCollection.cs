using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;

/// <summary>
///     One <see cref="ByInterfaceGraphQlTestFixture" /> for the tests of <c>runtime.byInterface</c> (AB#6932).
/// </summary>
[CollectionDefinition(Name)]
public class ByInterfaceCollection : ICollectionFixture<ByInterfaceGraphQlTestFixture>
{
    public const string Name = "ByInterface";
}
