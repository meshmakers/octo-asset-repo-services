using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;

/// <summary>AB#8598: the archive lifecycle CK methods, on a stream-data fixture of their own (they create and delete archives).</summary>
[CollectionDefinition(Name)]
public class ArchiveMethodCollection : ICollectionFixture<ArchiveMethodStreamDataFixture>
{
    public const string Name = "ArchiveMethod";
}
