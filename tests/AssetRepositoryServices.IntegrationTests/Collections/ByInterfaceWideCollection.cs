using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;

/// <summary>
///     One <see cref="ByInterfaceWideGraphQlTestFixture" /> for the budget check and the fan-out cap of
///     <c>runtime.byInterface</c> (AB#6932).
/// </summary>
[CollectionDefinition(Name)]
public class ByInterfaceWideCollection : ICollectionFixture<ByInterfaceWideGraphQlTestFixture>
{
    public const string Name = "ByInterfaceWide";
}
