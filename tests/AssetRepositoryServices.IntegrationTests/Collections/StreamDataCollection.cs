using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;

/// <summary>
///     Shares one <see cref="StreamDataFixture" /> - and therefore one MongoDB container AND one CrateDB container - across every test
///     class that joins this collection, replacing the per-class
///     <c>IClassFixture&lt;StreamDataFixture&gt;</c> (AB#4963).
/// </summary>
[CollectionDefinition(Name)]
public class StreamDataCollection : ICollectionFixture<StreamDataFixture>
{
    public const string Name = "StreamData";
}
