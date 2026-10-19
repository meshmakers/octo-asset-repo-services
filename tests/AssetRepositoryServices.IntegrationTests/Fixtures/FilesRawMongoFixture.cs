using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
///     The files fixture (System.Files imported, default root seeded) plus raw MongoDB access to the system tenant
///     database, for tests that read or write documents below the engine: the cross-replica name lease (AB#6885) and
///     the GridFS orphan sweep (AB#6541). Its own collection gives it its own database, because these tests drop and
///     seed collections directly.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class FilesRawMongoFixture : FilesTestFixture
{
    private IMongoDatabase? _database;

    /// <summary>
    ///     The system tenant database (the tenant the tests run against).
    /// </summary>
    public IMongoDatabase Database =>
        _database ??= new MongoClient(GetConnectionString()).GetDatabase(SystemDatabaseName);

    public IMongoCollection<BsonDocument> Collection(string name) => Database.GetCollection<BsonDocument>(name);

    public async Task<bool> CollectionExistsAsync(string name)
    {
        var names = await (await Database.ListCollectionNamesAsync(new ListCollectionNamesOptions
        {
            Filter = new BsonDocument("name", name)
        })).ToListAsync();
        return names.Count > 0;
    }
}
