using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;

/// <summary>
///     Platform file system tests that work on the raw tenant database (name lease AB#6885, GridFS orphan sweep
///     AB#6541) — own database, because they drop and seed collections directly.
/// </summary>
[CollectionDefinition(Name)]
public class FilesRawMongoCollection : ICollectionFixture<FilesRawMongoFixture>
{
    public const string Name = "FilesRawMongo";
}
