using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;

/// <summary>
///     Shares one <see cref="CkQueryTestFixture" /> - and therefore one MongoDB container plus the CK query sample model - across every test
///     class that joins this collection, replacing the per-class
///     <c>IClassFixture&lt;CkQueryTestFixture&gt;</c> (AB#4963).
/// </summary>
[CollectionDefinition(Name)]
public class CkQueryCollection : ICollectionFixture<CkQueryTestFixture>
{
    public const string Name = "CkQuery";
}
