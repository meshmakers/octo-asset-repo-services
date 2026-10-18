using System.Globalization;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Npgsql;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
///     A rollup of the fixture's raw archive with the statements the rollup consistency tests need (AB#6968): create and
///     recompute it, read what a reader sees, and reach into the CrateDB tables to seed or inspect a state. One kit per
///     test class; the rollups it creates are named by the caller and live in the fixture's schema.
/// </summary>
internal sealed class RollupTestKit
{
    /// <summary>The 20 raw points of the fixture lie in [10:00, 11:00): four 15-minute buckets.</summary>
    public static readonly DateTime RangeStart = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

    public static readonly DateTime RangeEnd = new(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc);
    public static readonly TimeSpan BucketSize = TimeSpan.FromMinutes(15);
    public const int ExpectedBuckets = 4;

    private readonly StreamDataFixture _fixture;

    public RollupTestKit(StreamDataFixture fixture, IStreamDataRepository repository)
    {
        _fixture = fixture;
        Repository = repository;
    }

    public IStreamDataRepository Repository { get; }

    public IArchiveRecomputeExecutor Executor => (IArchiveRecomputeExecutor)Repository;

    public static async Task<RollupTestKit> CreateAsync(StreamDataFixture fixture)
    {
        var systemContext = fixture.GetSystemContext();
        var tenantContext = await systemContext.FindTenantContextAsync(systemContext.TenantId);
        var repository = tenantContext.GetStreamDataRepository()
                         ?? throw new InvalidOperationException("StreamData repository not available.");
        return new RollupTestKit(fixture, repository);
    }

    /// <summary>Creates and activates a rollup (SUM of Voltage in 15-minute buckets) on the fixture's raw archive.</summary>
    public async Task<RollupHandle> CreateRollupAsync(string wellKnownName)
    {
        var systemContext = _fixture.GetSystemContext();
        var tenantContext = await systemContext.FindTenantContextAsync(systemContext.TenantId);
        var rollupLifecycle = tenantContext.GetRollupArchiveLifecycleService()
                              ?? throw new InvalidOperationException("Rollup lifecycle service not available.");
        var archiveLifecycle = tenantContext.GetArchiveLifecycleService()
                               ?? throw new InvalidOperationException("Archive lifecycle service not available.");
        var archiveStore = tenantContext.GetArchiveRuntimeStore();
        var rollupStore = tenantContext.GetRollupArchiveRuntimeStore()
                          ?? throw new InvalidOperationException("Rollup runtime store not available.");

        var rollupRtId = await rollupLifecycle.CreateAsync(
            wellKnownName,
            [new RollupSourceReference(_fixture.ArchiveRtId)],
            BucketSize,
            TimeSpan.Zero,
            new[] { new CkRollupAggregationSpec("Voltage", CkRollupFunction.Sum, null) });
        await archiveLifecycle.ActivateAsync(rollupRtId);

        var source = await archiveStore.GetAsync(_fixture.ArchiveRtId)
                     ?? throw new InvalidOperationException("Source archive snapshot missing.");
        var rollup = await rollupStore.GetAsync(rollupRtId)
                     ?? throw new InvalidOperationException("Rollup archive snapshot missing.");

        return new RollupHandle(rollupRtId, source, rollup,
            $"\"{_fixture.StreamDataTenantId}\".\"archive_{rollupRtId}\"",
            $"\"{_fixture.StreamDataTenantId}\".\"archive_{rollupRtId}__genmap\"");
    }

    /// <summary>One recompute of the whole range: writes the next generation, flips the pointer, sweeps (or defers the sweep).</summary>
    public async Task RecomputeAsync(RollupHandle rollup)
    {
        await Executor.ExecuteAsync(rollup.Source, rollup.Rollup, RangeStart, RangeEnd, rtIdScope: null,
            CancellationToken.None);
        await RefreshAsync(rollup.Table);
        await RefreshAsync(rollup.GenMapTable);
    }

    /// <summary>What a reader of the rollup sees: the Voltage sums of the rows of the active generation, ordered.</summary>
    public async Task<List<double>> ReadActiveValuesAsync(RollupHandle rollup)
    {
        var read = await Repository.ExecuteQueryAsync(rollup.RtId, StreamDataQueryOptions.Create()
            .WithCkTypeId(rollup.Rollup.TargetCkTypeId)
            .WithColumns(new List<string> { "voltage_sum" })
            .WithPagination(0, 10_000));
        return read.Rows
            .Select(r => Convert.ToDouble(r.Values["voltage_sum"], CultureInfo.InvariantCulture))
            .OrderBy(v => v)
            .ToList();
    }

    public async Task RefreshAsync(string qualifiedTable)
    {
        await using var conn = new NpgsqlConnection(_fixture.CrateDbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"REFRESH TABLE {qualifiedTable}", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(_fixture.CrateDbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<long> ScalarLongAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(_fixture.CrateDbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    public async Task<List<long>> DistinctGenerationsAsync(string qualifiedTable)
    {
        await using var conn = new NpgsqlConnection(_fixture.CrateDbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            $"SELECT DISTINCT \"generation\" FROM {qualifiedTable} ORDER BY \"generation\"", conn);
        var generations = new List<long>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            generations.Add(Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture));
        }

        return generations;
    }
}

/// <summary>A rollup of <see cref="RollupTestKit" />: its ids, snapshots and the qualified table names.</summary>
internal sealed record RollupHandle(
    OctoObjectId RtId, ArchiveSnapshot Source, RollupArchiveSnapshot Rollup, string Table, string GenMapTable);
