using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData;
using Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData.Configuration;
using Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData.Dtos;
using Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
/// Test fixture that provides MongoDB + CrateDB + full service stack for stream data integration tests.
/// Uses the system tenant for GraphQL execution (same pattern as GraphQlTestFixture) to get a fully
/// resolved CK cache. Enables stream data on the system tenant and inserts known test data points.
/// </summary>
public class StreamDataFixture : AssetRepoFixture
{
    private IContainer? _crateDbContainer;
    private IDocumentExecuter<OctoSchema>? _documentExecuter;
    private IGraphQLTextSerializer? _serializer;

    public string? CrateDbConnectionString { get; private set; }

    /// <summary>
    /// The tenant ID used for stream data CrateDB operations.
    /// This is the system tenant ID (no hyphens, safe for CrateDB table names).
    /// </summary>
    public string StreamDataTenantId => GetSystemContext().TenantId;

    /// <summary>
    /// Known test data: 20 data points with Voltage and Current attributes,
    /// timestamps spanning one hour at 3-minute intervals.
    /// </summary>
    public DateTime TestDataStartTime { get; } = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
    public DateTime TestDataEndTime { get; } = new(2026, 1, 1, 10, 57, 0, DateTimeKind.Utc);
    public int TestDataPointCount { get; } = 20;

    public string TestCkTypeId { get; } = "AssetRepositoryIntegrationTest/MeteringPoint";

    protected override async Task InitializeServicesAsync()
    {
        // Start CrateDB test container (single-node)
        _crateDbContainer = new ContainerBuilder("crate:5.10.10")
            .WithName($"cratedb-test-{Guid.NewGuid():N}")
            .WithPortBinding(5432, true)
            .WithPortBinding(4200, true)
            .WithEnvironment("CRATE_HEAP_SIZE", "512m")
            .WithCommand("-Cdiscovery.type=single-node")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged("started"))
            .Build();

        await _crateDbContainer.StartAsync();

        var crateDbPort = _crateDbContainer.GetMappedPublicPort(5432);
        CrateDbConnectionString = $"Host=localhost;Port={crateDbPort};Username=crate;SSL Mode=Prefer";

        // Register stream data services with single-node configuration
        Services.AddSingleton(new CrateDbTestConnectionString(CrateDbConnectionString));
        Services.AddStreamDataDatabase<TestStreamDataConfiguration>();

        // Call base which starts MongoDB, creates system tenant + test tenant, and builds the ServiceProvider
        await base.InitializeServicesAsync();

        var systemContext = GetSystemContext();

        // Enable stream data BEFORE importing the CK model.
        // EnableStreamDataAsync triggers repository access which eagerly loads the CK cache.
        // If we import the CK model first, the cache is invalidated by the import,
        // then EnableStreamDataAsync reloads it — but at that point ModelLoaderService.LoadAsync
        // may not resolve the newly imported model's types correctly.
        // By enabling first, the cache load only contains System (which is fine for stream data setup),
        // then the CK model import invalidates the cache, and the next access (GraphQL) reloads
        // with all models properly resolved.
        var tenantContext = await systemContext.FindTenantContextAsync(systemContext.TenantId);
        await tenantContext.EnableStreamDataAsync();

        // Import the CK model into the system tenant (same pattern as SampleDataFixture)
        var operationResult = new OperationResult();
        await systemContext.ImportCkModelAsync(
            new CkModelId("AssetRepositoryIntegrationTest"), operationResult);

        if (operationResult.HasErrors || operationResult.HasFatalErrors)
        {
            throw new InvalidOperationException(
                $"Failed to import AssetRepositoryIntegrationTest CK model: " +
                $"{string.Join(", ", operationResult.Messages.Select(m => m.MessageText))}");
        }

        // Insert known test data points into the system tenant's CrateDB table
        await InsertTestDataPoints();

        // Initialize GraphQL execution infrastructure
        _documentExecuter = Provider?.GetRequiredService<IDocumentExecuter<OctoSchema>>();
        _serializer = Provider?.GetRequiredService<IGraphQLTextSerializer>();
    }

    /// <summary>
    /// Executes a GraphQL query using the system tenant context.
    /// </summary>
    public async Task<ExecutionResult> ExecuteGraphQlAsync(string query, string? variables = null)
    {
        if (_documentExecuter == null)
        {
            throw new InvalidOperationException("GraphQL services not initialized");
        }

        Dictionary<string, object?>? inputs = null;
        if (variables != null)
        {
            using var doc = JsonDocument.Parse(variables);
            inputs = ConvertJsonElement(doc.RootElement) as Dictionary<string, object?>;
        }

        var result = await _documentExecuter.ExecuteAsync(options =>
        {
            options.Schema = null;
            options.Query = query;
            options.Variables = inputs != null ? new Inputs(inputs) : null;
            options.RequestServices = Provider;
            options.UserContext = new GraphQlUserContext(null, GetSystemContext());
        });

        return result;
    }

    public string SerializeGraphQl(ExecutionResult result)
    {
        if (_serializer == null)
        {
            throw new InvalidOperationException("GraphQL serializer not initialized");
        }

        return _serializer.Serialize(result);
    }

    private static object? ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(p => p.Name, p => ConvertJsonElement(p.Value)),
            JsonValueKind.Array => element.EnumerateArray()
                .Select(ConvertJsonElement)
                .ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.GetRawText()
        };
    }

    private async Task InsertTestDataPoints()
    {
        var databaseClient = GetService<IStreamDataDatabaseClient>();
        var tenantId = StreamDataTenantId;

        var dataPoints = new List<DataPointDto>();
        for (var i = 0; i < TestDataPointCount; i++)
        {
            var timestamp = TestDataStartTime.AddMinutes(i * 3);
            var voltage = 220.0 + (i * 0.5);
            var current = 10.0 + (i * 0.1);

            var attributes = new Dictionary<string, object?>
            {
                ["Voltage"] = voltage,
                ["Current"] = current
            };

            dataPoints.Add(new DataPointDto(attributes)
            {
                RtId = OctoObjectId.GenerateNewId(),
                CkTypeId = new RtCkId<CkTypeId>(TestCkTypeId),
                Timestamp = timestamp,
                RtWellKnownName = $"TestMeteringPoint{i:D3}",
            });
        }

        await databaseClient.InsertDataAsync(tenantId, dataPoints);

        // Explicit refresh to make data immediately queryable
        await RefreshTableAsync(tenantId);
    }

    private async Task RefreshTableAsync(string tableName)
    {
        await using var conn = new NpgsqlConnection(CrateDbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"REFRESH TABLE {tableName}", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Executes a stream-data query directly against the engine repository (bypassing GraphQL),
    /// used by invariant-pinning tests that inspect StreamDataRow.Values keys.
    /// </summary>
    public async Task<IReadOnlyList<StreamDataRow>> ExecuteRepoQueryDirectAsync(
        string ckTypeId,
        IReadOnlyList<string> columnPaths)
    {
        // Warm the CK cache — the repository's field resolver depends on the
        // system-tenant CK cache being populated, which normally happens via
        // GraphQL request pipeline. A trivial GraphQL query here triggers the
        // same cache-population path.
        _ = await ExecuteGraphQlAsync("{ __typename }");

        var ckId = new RtCkId<CkTypeId>(ckTypeId);
        var options = StreamDataQueryOptions.Create()
            .WithCkTypeId(ckId)
            .WithColumns(columnPaths.ToList())
            .WithPagination(0, 10);

        var systemContext = GetSystemContext();
        var tenantContext = await systemContext.FindTenantContextAsync(systemContext.TenantId);
        var repo = tenantContext.GetStreamDataRepository()
            ?? throw new InvalidOperationException("stream-data not enabled");
        var result = await repo.ExecuteQueryAsync(options);
        return result.Rows;
    }

    protected override async Task DisposeServicesAsync()
    {
        await base.DisposeServicesAsync();

        if (_crateDbContainer != null)
        {
            await _crateDbContainer.StopAsync();
            await _crateDbContainer.DisposeAsync();
        }
    }

    internal record CrateDbTestConnectionString(string ConnectionString);

    /// <summary>
    /// Configures stream data for single-node CrateDB testcontainer:
    /// 1 shard, 0 replicas (3 shards silently drops rows on single-node).
    /// </summary>
    internal class TestStreamDataConfiguration(CrateDbTestConnectionString testConnection)
        : IConfigureNamedOptions<StreamDataConfiguration>
    {
        public void Configure(StreamDataConfiguration options)
        {
            Configure(Options.DefaultName, options);
        }

        public void Configure(string? name, StreamDataConfiguration options)
        {
            options.ConnectionString = testConnection.ConnectionString;
            options.NumberOfShards = 1;
            options.NumberOfReplicas = 0;
        }
    }
}
