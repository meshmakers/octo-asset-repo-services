using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
/// Test fixture that provides MongoDB + CrateDB + full service stack for stream data integration
/// tests. Uses the system tenant so the CK cache resolves the test model. After T17 the fixture
/// owns the full archive lifecycle: it creates a <c>CkArchive</c> runtime entity with explicit
/// columns, activates it (which provisions the per-archive CrateDB table via
/// <see cref="IArchiveLifecycleService"/>), then writes test data through
/// <see cref="IStreamDataRepository.InsertAsync(OctoObjectId, StreamDataPoint)"/>. The resulting
/// archive id is exposed as <see cref="ArchiveRtId"/> and threaded through every GraphQL query
/// in the tests.
/// </summary>
public class StreamDataFixture : AssetRepoFixture
{
    private IContainer? _crateDbContainer;
    private IDocumentExecuter<OctoSchema>? _documentExecuter;
    private IGraphQLTextSerializer? _serializer;

    public string? CrateDbConnectionString { get; private set; }

    /// <summary>
    /// The tenant ID used for stream data CrateDB operations (system tenant — name contains no
    /// hyphens, safe for CrateDB schema identifiers).
    /// </summary>
    public string StreamDataTenantId => GetSystemContext().TenantId;

    /// <summary>
    /// Known test data: 20 data points with voltage and current attributes, timestamps spanning
    /// roughly an hour at 3-minute intervals.
    /// </summary>
    public DateTime TestDataStartTime { get; } = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
    public DateTime TestDataEndTime { get; } = new(2026, 1, 1, 10, 57, 0, DateTimeKind.Utc);
    public int TestDataPointCount { get; } = 20;

    public string TestCkTypeId { get; } = "AssetRepositoryIntegrationTest/MeteringPoint";

    /// <summary>
    /// Runtime id of the <c>CkArchive</c> entity provisioned in <see cref="InitializeServicesAsync"/>.
    /// Tests pass this through GraphQL as the <c>archiveRtId</c> argument so every query targets
    /// the per-archive CrateDB table the fixture writes to.
    /// </summary>
    public OctoObjectId ArchiveRtId { get; private set; }

    /// <summary>String form of <see cref="ArchiveRtId"/> for inline embedding in GraphQL bodies.</summary>
    public string ArchiveRtIdString => ArchiveRtId.ToString();

    // ── Windowed (TimeRange) archive — AB#4246 regression ────────────────────────────────────
    // 24 contiguous 15-minute windows over 6 hours, single series. Used to prove that downsampling
    // with limit >= the number of distinct windows no longer nulls out every bin (the windowed
    // fully-contained bug: a bin finer than the source window dropped every window).
    public DateTime WindowedStartTime { get; } = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
    public int WindowedWindowCount { get; } = 24;
    public TimeSpan WindowedWindowSize { get; } = TimeSpan.FromMinutes(15);

    /// <summary>Exclusive upper bound of the last window.</summary>
    public DateTime WindowedEndTime => WindowedStartTime.Add(WindowedWindowSize * WindowedWindowCount);

    /// <summary>Runtime id of the windowed <c>TimeRangeArchive</c> provisioned by the fixture.</summary>
    public OctoObjectId WindowedArchiveRtId { get; private set; }

    public string WindowedArchiveRtIdString => WindowedArchiveRtId.ToString();

    private readonly OctoObjectId _windowedSeriesRtId = OctoObjectId.GenerateNewId();

    protected override async Task InitializeServicesAsync()
    {
        // Start CrateDB test container (single-node).
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

        // Flip the instance-level kill switch BEFORE registering the stream data stack — the
        // tenant context refuses to enable stream data if `StreamData:Enabled` is false at
        // process scope (concept §5 two-tier activation).
        Services.Configure<StreamDataInstanceConfiguration>(c => c.Enabled = true);
        Services.AddSingleton(new CrateDbTestConnectionString(CrateDbConnectionString));
        Services.AddStreamDataDatabase<TestStreamDataConfiguration>();

        // Mirrors Program.cs: register the descriptor so EnsureStreamDataCkModelImportedAsync
        // resolves the shipped model id instead of the engine's hardcoded 1.0.0 fallback,
        // which no catalog ships.
        Services.AddSingleton<IStreamDataCkModelDescriptor>(
            _ => new StreamDataCkModelDescriptor(SystemStreamDataCkIds.CkModelId));

        // Call base which starts MongoDB, creates system tenant + test tenant, builds the SP.
        await base.InitializeServicesAsync();

        var systemContext = GetSystemContext();

        // Enable stream data BEFORE importing the CK model — the import otherwise invalidates the
        // CK cache mid-flight which makes EnableStreamDataAsync's eager cache load see a stale
        // model. (Same ordering subtlety as the original fixture; preserved.)
        var tenantContext = await systemContext.FindTenantContextAsync(systemContext.TenantId);
        await tenantContext.EnableStreamDataAsync();

        var operationResult = new OperationResult();
        await systemContext.ImportCkModelAsync(
            new CkModelId("AssetRepositoryIntegrationTest"), operationResult);

        if (operationResult.HasErrors || operationResult.HasFatalErrors)
        {
            throw new InvalidOperationException(
                $"Failed to import AssetRepositoryIntegrationTest CK model: " +
                $"{string.Join(", ", operationResult.Messages.Select(m => m.MessageText))}");
        }

        // Provision a real archive: create the CkArchive entity, then drive it through Activated
        // so the CrateDB table is materialised per its column spec.
        ArchiveRtId = await CreateAndActivateArchiveAsync();

        // Write the canonical test points through the repository so the per-archive table sees the
        // exact payload shape that production callers produce (StreamDataPoint → DataPointDto with
        // camelCase keys after T17).
        await InsertTestDataPoints();

        // Provision a windowed TimeRange archive + windowed data for the AB#4246 downsampling test.
        WindowedArchiveRtId = await CreateAndActivateWindowedArchiveAsync();
        await InsertWindowedDataPoints();

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

    private async Task<OctoObjectId> CreateAndActivateArchiveAsync()
    {
        var systemContext = GetSystemContext();
        var tenantRepository = systemContext.GetSystemTenantRepository();
        // System.StreamData 1.2.0 split the original concrete `CkArchive` into the abstract
        // `Archive` base + concrete `RawArchive` subtype — `Archive` is no longer instantiable.
        // Fresh-imports therefore use `RtRawArchive` directly.
        var archive = new RtRawArchive
        {
            RtWellKnownName = "MeteringPointArchive",
            TargetCkTypeId = TestCkTypeId,
            Status = RtCkArchiveStatusEnum.Created,
            Columns = new AttributeRecordValueList<RtCkArchiveColumnRecord>
            {
                new() { Path = "Voltage", Indexed = true, Required = false },
                new() { Path = "Current", Indexed = true, Required = false },
                // Enum-typed column (OperatingStatus) — drives the AB#4247 enum-name resolution
                // tests. Additive: existing tests query Voltage/Current only and are unaffected.
                new() { Path = "OperatingStatus", Indexed = true, Required = false }
            }
        };

        using (var session = await tenantRepository.GetSessionAsync())
        {
            session.StartTransaction();
            await tenantRepository.InsertOneRtEntityAsync(session, archive);
            await session.CommitTransactionAsync();
        }

        var tenantContext = await systemContext.FindTenantContextAsync(systemContext.TenantId);
        var lifecycle = tenantContext.GetArchiveLifecycleService()
            ?? throw new InvalidOperationException("ArchiveLifecycleService not registered.");
        await lifecycle.ActivateAsync(archive.RtId);
        return archive.RtId;
    }

    private async Task InsertTestDataPoints()
    {
        var systemContext = GetSystemContext();
        var tenantContext = await systemContext.FindTenantContextAsync(systemContext.TenantId);
        var repo = tenantContext.GetStreamDataRepository()
            ?? throw new InvalidOperationException("StreamDataRepository not available — was EnableStreamDataAsync called?");

        var ckTypeId = new RtCkId<CkTypeId>(TestCkTypeId);
        var points = new List<StreamDataPoint>(TestDataPointCount);
        for (var i = 0; i < TestDataPointCount; i++)
        {
            var timestamp = TestDataStartTime.AddMinutes(i * 3);
            var attributes = new Dictionary<string, object?>
            {
                // Keys reflect the picker output (camelCase paths). T17 path-to-column mapping
                // produces matching column names: `voltage`, `current`, `operatingstatus`.
                ["voltage"] = 220.0 + (i * 0.5),
                ["current"] = 10.0 + (i * 0.1),
                // OperatingStatus enum keys cycle Unknown(0) / OK(1) / Maintenance(2) so the
                // AB#4247 enum-resolution tests see a representative spread (max key = 2).
                ["operatingStatus"] = i % 3
            };
            points.Add(new StreamDataPoint
            {
                RtId = OctoObjectId.GenerateNewId(),
                CkTypeId = ckTypeId,
                Timestamp = timestamp,
                RtWellKnownName = $"TestMeteringPoint{i:D3}",
                Attributes = attributes
            });
        }

        await repo.InsertAsync(ArchiveRtId, points);
        await RefreshArchiveTableAsync();
    }

    /// <summary>
    /// CrateDB applies inserts asynchronously to the read path (~1s). Tests need read-after-write
    /// consistency so we force a refresh on the per-archive table after seeding.
    /// </summary>
    private Task RefreshArchiveTableAsync() => RefreshArchiveTableAsync(ArchiveRtIdString);

    private async Task RefreshArchiveTableAsync(string archiveRtIdString)
    {
        var qualifiedTable = $"\"{StreamDataTenantId}\".\"archive_{archiveRtIdString}\"";
        await using var conn = new NpgsqlConnection(CrateDbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"REFRESH TABLE {qualifiedTable}", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Creates and activates a windowed <c>TimeRangeArchive</c> (one Voltage column). Counterpart of
    /// <see cref="CreateAndActivateArchiveAsync"/> for the windowed-storage path (AB#4246).
    /// </summary>
    private async Task<OctoObjectId> CreateAndActivateWindowedArchiveAsync()
    {
        var systemContext = GetSystemContext();
        var tenantContext = await systemContext.FindTenantContextAsync(systemContext.TenantId);
        var store = tenantContext.GetTimeRangeArchiveRuntimeStore()
            ?? throw new InvalidOperationException("TimeRangeArchiveRuntimeStore not available — was EnableStreamDataAsync called?");

        var rtId = await store.InsertAsync(
            "WindowedMeteringPointArchive",
            new RtCkId<CkTypeId>(TestCkTypeId),
            new List<CkArchiveColumnSpec> { new("Voltage", Indexed: true, Required: false) },
            WindowedWindowSize);

        var lifecycle = tenantContext.GetArchiveLifecycleService()
            ?? throw new InvalidOperationException("ArchiveLifecycleService not registered.");
        await lifecycle.ActivateAsync(rtId);
        return rtId;
    }

    /// <summary>
    /// Writes <see cref="WindowedWindowCount"/> contiguous fixed-width windows through
    /// <see cref="IStreamDataRepository.InsertTimeRangeAsync"/> (the windowed write path), single series.
    /// </summary>
    private async Task InsertWindowedDataPoints()
    {
        var systemContext = GetSystemContext();
        var tenantContext = await systemContext.FindTenantContextAsync(systemContext.TenantId);
        var repo = tenantContext.GetStreamDataRepository()
            ?? throw new InvalidOperationException("StreamDataRepository not available — was EnableStreamDataAsync called?");

        var ckTypeId = new RtCkId<CkTypeId>(TestCkTypeId);
        var points = new List<TimeRangeStreamDataPoint>(WindowedWindowCount);
        for (var k = 0; k < WindowedWindowCount; k++)
        {
            var from = WindowedStartTime.Add(WindowedWindowSize * k);
            var to = from.Add(WindowedWindowSize);
            points.Add(new TimeRangeStreamDataPoint
            {
                RtId = _windowedSeriesRtId,
                CkTypeId = ckTypeId,
                From = from,
                To = to,
                RtWellKnownName = "WindowedSeries",
                Attributes = new Dictionary<string, object?> { ["voltage"] = 100.0 + k }
            });
        }

        await repo.InsertTimeRangeAsync(WindowedArchiveRtId, points);
        await RefreshArchiveTableAsync(WindowedArchiveRtIdString);
    }

    /// <summary>
    /// Executes a stream-data query directly against the engine repository (bypassing GraphQL).
    /// Used by invariant-pinning tests that inspect <c>StreamDataRow.Values</c> keys.
    /// </summary>
    public async Task<IReadOnlyList<StreamDataRow>> ExecuteRepoQueryDirectAsync(
        string ckTypeId,
        IReadOnlyList<string> columnPaths)
    {
        // Warm the CK cache via a no-op GraphQL request — the repository's field resolver depends
        // on the system-tenant CK cache being populated.
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
        var result = await repo.ExecuteQueryAsync(ArchiveRtId, options);
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
