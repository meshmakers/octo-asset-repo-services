using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;

/// <summary>
///     Platform file system tests (AB#6171) — one fixture with System.Files imported.
/// </summary>
[CollectionDefinition(Name)]
public class FilesCollection : ICollectionFixture<FilesTestFixture>
{
    public const string Name = "Files";
}
