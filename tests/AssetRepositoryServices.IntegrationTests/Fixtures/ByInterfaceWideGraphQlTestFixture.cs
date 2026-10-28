using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
///     AB#6932: GraphQL host for the budget check and the fan-out cap of <c>runtime.byInterface</c>. Two generated
///     CK language 2 models next to the test model: <c>Perf</c> (30 independent collection roots, all implementing
///     <c>Named-1</c> with an index on Name; <c>T01</c> also implements <c>Solo-1</c>) and <c>Wide</c> (65 roots, one
///     more than the cap).
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class ByInterfaceWideGraphQlTestFixture : GraphQlTestFixture
{
    public const int PerfRoots = 30;
    public const int WideRoots = 65;
    public const string PerfModel = "Perf";
    public const string WideModel = "Wide";

    private IMongoDatabase? _database;

    protected override async Task InitializeServicesAsync()
    {
        await base.InitializeServicesAsync();

        var systemContext = GetSystemContext();
        var systemId = await GetInstalledSystemIdAsync();
        await systemContext.ImportCkModelAsync(
            InterfaceQueryWideModel.Build(systemId, PerfModel, PerfRoots, indexName: true, soloInterface: true));
        await systemContext.ImportCkModelAsync(
            InterfaceQueryWideModel.Build(systemId, WideModel, WideRoots, indexName: false));
        _database = new MongoClient(GetConnectionString()).GetDatabase(SystemDatabaseName);
    }

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

    /// <summary>The raw collection of a generated type (<c>Perf/T01</c> is stored in <c>RtEntity_PerfT01</c>).</summary>
    public IMongoCollection<BsonDocument> RawCollection(string model, int root) =>
        _database!.GetCollection<BsonDocument>($"RtEntity_{model}T{root:D2}");

    public async Task<List<string>> IndexKeysAsync(string model, int root)
    {
        var cursor = await RawCollection(model, root).Indexes.ListAsync();
        return (await cursor.ToListAsync()).Select(i => i["key"].ToJson()).ToList();
    }
}
