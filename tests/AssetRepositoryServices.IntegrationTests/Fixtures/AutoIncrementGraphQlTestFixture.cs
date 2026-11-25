using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
///     GraphQL host with an auto-numbered type. The generated model <c>AutoIncrementGql</c> has one collection
///     root <c>Ticket</c> (Title, Number from the counter <see cref="CounterName" />, an optional linked binary
///     Attachment whose bytes go to GridFS); the counter starts at
///     <see cref="CounterStart" /> (the first number handed out is <c>CounterStart + 1</c>).
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class AutoIncrementGraphQlTestFixture : GraphQlTestFixture
{
    public const string TicketCkTypeId = "AutoIncrementGql/Ticket";
    public const string CounterName = "AutoIncrementGqlTicketNumber";
    public const int CounterStart = 1000;

    private const string ModelName = "AutoIncrementGql";

    private IMongoDatabase? _database;

    protected override async Task InitializeServicesAsync()
    {
        await base.InitializeServicesAsync();

        var systemContext = GetSystemContext();
        await systemContext.ImportCkModelAsync(TicketModel(await GetInstalledSystemIdAsync()));

        var repository = systemContext.GetTenantRepository();
        using (var session = await repository.GetSessionAsync())
        {
            session.StartTransaction();
            var counter = await repository.CreateTransientRtEntityAsync<RtAutoIncrement>();
            counter.RtWellKnownName = CounterName;
            counter.CurrentValue = CounterStart;
            counter.End = int.MaxValue;
            await repository.InsertOneRtEntityAsync(session, counter);
            await session.CommitTransactionAsync();
        }

        _database = new MongoClient(GetConnectionString()).GetDatabase(SystemDatabaseName);
    }

    /// <summary>The raw collection of the tickets.</summary>
    public IMongoCollection<BsonDocument> Tickets => _database!.GetCollection<BsonDocument>($"RtEntity_{ModelName}Ticket");

    /// <summary>The stored number of every ticket with the given title prefix (raw, from MongoDB).</summary>
    public async Task<List<long>> NumbersAsync(string titlePrefix)
    {
        var filter = Builders<BsonDocument>.Filter.Regex("attributes.title",
            new BsonRegularExpression("^" + global::System.Text.RegularExpressions.Regex.Escape(titlePrefix)));
        var documents = await Tickets.Find(filter).ToListAsync();
        return documents.Select(d => d["attributes"]["number"].ToInt64()).ToList();
    }

    /// <summary>Number of GridFS files in the system tenant database.</summary>
    public Task<long> CountGridFsFilesAsync() =>
        _database!.GetCollection<BsonDocument>("fs.files").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);

    private async Task<CkModelId> GetInstalledSystemIdAsync()
    {
        var systemContext = GetSystemContext();
        var catalogService = GetService<ICatalogService>();
        foreach (var version in (await catalogService.ListVersionsAsync("System")).OrderByDescending(v => v.ModelId))
        {
            if (await systemContext.IsCkModelExistingAsync(version.ModelId))
            {
                return version.ModelId;
            }
        }

        throw new InvalidOperationException("No installed System model found.");
    }

    private static CkCompiledModelRoot TicketModel(CkModelId system)
    {
        var id = new CkModelId($"{ModelName}-1.0.0");
        return new CkCompiledModelRoot
        {
            ModelId = id,
            Description = "Parallel GraphQL creations of an auto-numbered type",
            Dependencies = [system],
            Attributes =
            [
                new CkAttributeDto { AttributeId = new CkAttributeId("Title-1"), ValueType = AttributeValueTypesDto.String },
                new CkAttributeDto { AttributeId = new CkAttributeId("Number-1"), ValueType = AttributeValueTypesDto.Int },
                new CkAttributeDto
                {
                    AttributeId = new CkAttributeId("Attachment-1"), ValueType = AttributeValueTypesDto.BinaryLinked
                }
            ],
            Types =
            [
                new CkCompiledTypeDto
                {
                    TypeId = new CkTypeId("Ticket-1"), IsCollectionRoot = true,
                    DerivedFromCkTypeId = new CkId<CkTypeId>(system, new CkTypeId("Entity-1")),
                    Attributes =
                    [
                        new CkTypeAttributeDto
                        {
                            CkAttributeId = new CkId<CkAttributeId>(id, new CkAttributeId("Title-1")),
                            AttributeName = "Title"
                        },
                        new CkTypeAttributeDto
                        {
                            CkAttributeId = new CkId<CkAttributeId>(id, new CkAttributeId("Number-1")),
                            AttributeName = "Number", IsOptional = true, AutoIncrementReference = CounterName
                        },
                        new CkTypeAttributeDto
                        {
                            CkAttributeId = new CkId<CkAttributeId>(id, new CkAttributeId("Attachment-1")),
                            AttributeName = "Attachment", IsOptional = true
                        }
                    ]
                }
            ]
        };
    }
}
