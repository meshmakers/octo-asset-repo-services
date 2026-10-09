using System.Text;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files.Migration;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.GridFS;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
///     Fixture of the System.Reporting → System.Files migration tests (AB#6175): the files fixture (System.Files
///     imported, default root seeded) plus raw MongoDB access to the system tenant database, where legacy
///     System.Reporting file data is seeded exactly as Reporting 2.x stored it — without the System.Reporting
///     model, which is what the sweep has to cope with once System.Reporting 3.0.0 has dropped the types.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class FilesMigrationTestFixture : FilesTestFixture
{
    /// <summary>
    ///     Test-model customer from the sample data, used as origin of a "Related" link to a legacy file.
    /// </summary>
    public const string CustomerRtId = "67000001aaaa1111bbbb0001";

    public const string CustomerCkTypeId = "AssetRepositoryIntegrationTest/Customer";

    private IMongoDatabase? _database;

    /// <summary>
    ///     The system tenant database (the tenant the tests run against).
    /// </summary>
    public IMongoDatabase Database =>
        _database ??= new MongoClient(GetConnectionString()).GetDatabase(SystemDatabaseName);

    public ReportingFilesMoveSweep Sweep => GetService<ReportingFilesMoveSweep>();

    public ReportingFilesSweepRunner Runner => GetService<ReportingFilesSweepRunner>();

    public ReportingFilesSweepTracker Tracker => GetService<ReportingFilesSweepTracker>();

    public FilesMigrationStatusService Status => GetService<FilesMigrationStatusService>();

    public IMongoCollection<BsonDocument> Collection(string name) => Database.GetCollection<BsonDocument>(name);

    /// <summary>
    ///     Seeds a legacy tree: root → folder "Docs" → item "a.txt", item "b.txt" directly under the root, an
    ///     item without parent, and a "Related" link from the sample customer to "a.txt". Each item has a real
    ///     GridFS file stamped <c>System.Reporting/FileSystemItem@&lt;rtId&gt;</c>.
    /// </summary>
    public async Task<LegacyTree> SeedLegacyTreeAsync(string? rootWellKnownName = null)
    {
        rootWellKnownName ??= "LegacyRoot" + Guid.NewGuid().ToString("N")[..8];
        var root = await InsertLegacyEntityAsync(ReportingFilesMigrationConstants.LegacyFolderRootCkTypeId,
            rootWellKnownName, rootWellKnownName);
        var folder = await InsertLegacyEntityAsync(ReportingFilesMigrationConstants.LegacyFolderCkTypeId, "Docs");
        await InsertAssociationAsync(folder, ReportingFilesMigrationConstants.LegacyFolderCkTypeId, root,
            ReportingFilesMigrationConstants.LegacyFolderRootCkTypeId, "System/ParentChild");

        var (itemA, binaryA) = await InsertLegacyItemAsync("a.txt", "content of a", folder,
            ReportingFilesMigrationConstants.LegacyFolderCkTypeId);
        var (itemB, binaryB) = await InsertLegacyItemAsync("b.txt", "content of b", root,
            ReportingFilesMigrationConstants.LegacyFolderRootCkTypeId);
        var (orphan, binaryOrphan) = await InsertLegacyItemAsync("orphan.txt", "no parent", null, null);

        await InsertAssociationAsync(ObjectId.Parse(CustomerRtId), CustomerCkTypeId, itemA,
            ReportingFilesMigrationConstants.LegacyFileSystemItemCkTypeId, "System/Related");

        return new LegacyTree(rootWellKnownName, root, folder, itemA, binaryA, itemB, binaryB, orphan, binaryOrphan);
    }

    /// <summary>
    ///     Inserts one legacy item (entity + GridFS file + optional ParentChild link).
    /// </summary>
    public async Task<(ObjectId Item, ObjectId Binary)> InsertLegacyItemAsync(string name, string content,
        ObjectId? parent, string? parentCkTypeId)
    {
        var rtId = ObjectId.GenerateNewId();
        var bytes = Encoding.UTF8.GetBytes(content);
        var bucket = new GridFSBucket(Database);
        var binaryId = await bucket.UploadFromBytesAsync(name, bytes, new GridFSUploadOptions
        {
            Metadata = new BsonDocument
            {
                { "contentType", "text/plain" },
                { "binaryType", 0 },
                { "expiryDateTime", BsonNull.Value },
                { "rtEntityId", $"{ReportingFilesMigrationConstants.LegacyFileSystemItemCkTypeId}@{rtId}" }
            }
        });

        var attributes = new BsonDocument
        {
            { "name", name },
            {
                "content", new BsonDocument
                {
                    { "_t", "EntityBinaryInfo" },
                    { "contentType", "text/plain" },
                    { "binaryId", binaryId },
                    { "filename", name },
                    { "size", bytes.Length }
                }
            }
        };
        await InsertLegacyDocumentAsync(rtId, ReportingFilesMigrationConstants.LegacyFileSystemItemCkTypeId,
            attributes, null);
        if (parent != null)
        {
            await InsertAssociationAsync(rtId, ReportingFilesMigrationConstants.LegacyFileSystemItemCkTypeId,
                parent.Value, parentCkTypeId!, "System/ParentChild");
        }

        return (rtId, binaryId);
    }

    public async Task<ObjectId> InsertLegacyEntityAsync(string ckTypeId, string name, string? wellKnownName = null)
    {
        var rtId = ObjectId.GenerateNewId();
        await InsertLegacyDocumentAsync(rtId, ckTypeId, new BsonDocument("name", name), wellKnownName);
        return rtId;
    }

    private async Task InsertLegacyDocumentAsync(ObjectId rtId, string ckTypeId, BsonDocument attributes,
        string? wellKnownName)
    {
        var now = DateTime.UtcNow;
        var document = new BsonDocument
        {
            { "_id", rtId },
            { "_t", "RtEntity" },
            { "attributes", attributes },
            { "rtCreationDateTime", now },
            { "rtChangedDateTime", now },
            { "ckTypeId", ckTypeId }
        };
        if (wellKnownName != null)
        {
            document.Add("rtWellKnownName", wellKnownName);
        }

        document.Add("rtDisplayName", BsonNull.Value);
        document.Add("rtDisplayDescription", BsonNull.Value);
        document.Add("rtVersion", 0);
        await Collection(ReportingFilesMigrationConstants.LegacyEntityCollectionName).InsertOneAsync(document);
    }

    public async Task<ObjectId> InsertAssociationAsync(ObjectId originRtId, string originCkTypeId,
        ObjectId targetRtId, string targetCkTypeId, string roleId)
    {
        var id = ObjectId.GenerateNewId();
        await Collection(ReportingFilesMigrationConstants.AssociationCollectionName).InsertOneAsync(new BsonDocument
        {
            { "_id", id },
            { "attributes", new BsonDocument() },
            { "originRtId", originRtId },
            { "originCkTypeId", originCkTypeId },
            { "targetRtId", targetRtId },
            { "targetCkTypeId", targetCkTypeId },
            { "associationRoleId", roleId }
        });
        return id;
    }

    public async Task<bool> CollectionExistsAsync(string name)
    {
        var names = await (await Database.ListCollectionNamesAsync(new ListCollectionNamesOptions
        {
            Filter = new BsonDocument("name", name)
        })).ToListAsync();
        return names.Count > 0;
    }
}

/// <summary>
///     Ids of a seeded legacy tree.
/// </summary>
public sealed record LegacyTree(
    string RootWellKnownName,
    ObjectId Root,
    ObjectId Folder,
    ObjectId ItemA,
    ObjectId BinaryA,
    ObjectId ItemB,
    ObjectId BinaryB,
    ObjectId Orphan,
    ObjectId BinaryOrphan);
