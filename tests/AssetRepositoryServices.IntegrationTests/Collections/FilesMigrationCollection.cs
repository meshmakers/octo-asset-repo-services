using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;

/// <summary>
///     System.Reporting → System.Files migration tests (AB#6175) — own database, because the sweep moves every
///     legacy file entity of the tenant.
/// </summary>
[CollectionDefinition(Name)]
public class FilesMigrationCollection : ICollectionFixture<FilesMigrationTestFixture>
{
    public const string Name = "FilesMigration";
}
