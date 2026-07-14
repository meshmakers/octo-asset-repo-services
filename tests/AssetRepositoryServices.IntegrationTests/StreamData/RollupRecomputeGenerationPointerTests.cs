using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Npgsql;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
/// AB#4184 Phase 6 — end-to-end validation of the per-window generation-pointer atomic swap against a
/// REAL CrateDB (Testcontainer). Drives the production <see cref="IArchiveRecomputeExecutor"/> against
/// a rollup built on the fixture's raw archive and asserts, via direct CrateDB queries plus the
/// repository read path, that: a recompute writes the next generation and flips the pointer; the read
/// path returns exactly the active generation per window (no mixed read, staged-but-not-pointed rows
/// stay hidden); and the post-flip sweep removes every superseded generation.
///
/// This is the automated counterpart to the previously-manual live validation — it pins the Phase-6
/// SQL (generation column + PK, ON CONFLICT key, INSERT-at-generation, genmap flip, CASE read filter,
/// sweep) so it cannot regress.
/// </summary>
[Collection("Sequential")]
public class RollupRecomputeGenerationPointerTests(StreamDataFixture fixture, ITestOutputHelper output)
    : IClassFixture<StreamDataFixture>
{
    private static readonly DateTime RangeStart = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime RangeEnd = new(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan BucketSize = TimeSpan.FromMinutes(15); // 4 buckets over [10:00, 11:00)

    // WindowsProcessed counts buckets (4); the rollup ROW count is independent — the fixture's 20
    // raw points each carry a distinct rtId, so GROUP BY rtId yields one rollup row per point.
    // The row count is derived at runtime to stay robust against the fixture's data shape.
    private const int ExpectedBuckets = 4;

    [Fact]
    public async Task Recompute_FlipsGenerationPointer_HidesUncommittedRows_AndSweepsSuperseded()
    {
        fixture.OutputHelper = output;

        var systemContext = fixture.GetSystemContext();
        var tenantContext = await systemContext.FindTenantContextAsync(systemContext.TenantId);

        var rollupLifecycle = tenantContext.GetRollupArchiveLifecycleService()
            ?? throw new InvalidOperationException("Rollup lifecycle service not available.");
        var archiveLifecycle = tenantContext.GetArchiveLifecycleService()
            ?? throw new InvalidOperationException("Archive lifecycle service not available.");
        var archiveStore = tenantContext.GetArchiveRuntimeStore();
        var rollupStore = tenantContext.GetRollupArchiveRuntimeStore()
            ?? throw new InvalidOperationException("Rollup runtime store not available.");
        var repo = tenantContext.GetStreamDataRepository()
            ?? throw new InvalidOperationException("StreamData repository not available.");
        var executor = (IArchiveRecomputeExecutor)repo;

        // ── Create + activate a rollup on the fixture's raw archive (SUM(Voltage), 15-min buckets) ──
        var rollupRtId = await rollupLifecycle.CreateAsync(
            rtWellKnownName: "P6GenPointerRollup",
            sourceArchiveRtId: fixture.ArchiveRtId,
            bucketSize: BucketSize,
            watermarkLag: TimeSpan.Zero,
            aggregations: new[] { new CkRollupAggregationSpec("Voltage", CkRollupFunction.Sum, null) });
        await archiveLifecycle.ActivateAsync(rollupRtId);

        var sourceSnapshot = await archiveStore.GetAsync(fixture.ArchiveRtId)
            ?? throw new InvalidOperationException("Source archive snapshot missing.");
        var rollupSnapshot = await rollupStore.GetAsync(rollupRtId)
            ?? throw new InvalidOperationException("Rollup archive snapshot missing.");

        var rollupTable = $"\"{fixture.StreamDataTenantId}\".\"archive_{rollupRtId}\"";
        var genMapTable = $"\"{fixture.StreamDataTenantId}\".\"archive_{rollupRtId}__genmap\"";

        // genmap exists and is empty right after activation (steady state).
        (await ScalarLongAsync($"SELECT count(*) FROM {genMapTable}")).Should().Be(0,
            "the generation map is created empty at activation");

        // ── Recompute #1: aggregates the source into the rollup under generation 1, flips, sweeps ──
        var result1 = await executor.ExecuteAsync(
            sourceSnapshot, rollupSnapshot, RangeStart, RangeEnd, rtIdScope: null, CancellationToken.None);
        await RefreshAsync(rollupTable);
        await RefreshAsync(genMapTable);

        result1.WindowsProcessed.Should().Be(ExpectedBuckets);

        // Pointer flipped to generation 1 for the whole range.
        (await ScalarLongAsync($"SELECT count(*) FROM {genMapTable}")).Should().Be(1);
        (await ScalarLongAsync($"SELECT \"generation\" FROM {genMapTable}")).Should().Be(1);
        (await ScalarLongAsync($"SELECT \"range_start\" FROM {genMapTable}"))
            .Should().Be(new DateTimeOffset(RangeStart).ToUnixTimeMilliseconds());

        // Every recomputed row is generation 1; capture the row count for the rest of the assertions.
        (await DistinctGenerationsAsync(rollupTable)).Should().Equal(1L);
        var generationOneRows = await ScalarLongAsync(
            $"SELECT count(*) FROM {rollupTable} WHERE \"generation\" = 1");
        generationOneRows.Should().BeGreaterThan(0, "the recompute aggregated the source into the rollup");

        // Read path returns exactly the active (generation-1) rows.
        var read1 = await repo.ExecuteQueryAsync(rollupRtId, StreamDataQueryOptions.Create()
            .WithCkTypeId(rollupSnapshot.TargetCkTypeId)
            .WithColumns(new List<string> { "voltage_sum" })
            .WithPagination(0, 1000));
        read1.Rows.Count.Should().Be((int)generationOneRows);

        // ── Inject a NOT-yet-committed higher-generation row (simulates staged-before-flip state) ──
        // generation 999 is not referenced by the genmap (still -> 1), so the read path must hide it.
        await ExecuteAsync(
            $"INSERT INTO {rollupTable} (\"window_start\",\"window_end\",\"rtid\",\"cktypeid\"," +
            $"\"rtwellknownname\",\"was_updated\",\"voltage_sum\",\"generation\") " +
            $"SELECT \"window_start\",\"window_end\",\"rtid\",\"cktypeid\",\"rtwellknownname\",\"was_updated\"," +
            $"\"voltage_sum\",999 FROM {rollupTable} WHERE \"generation\" = 1 LIMIT 1");
        await RefreshAsync(rollupTable);

        // Raw table now has the extra row, but the read path still returns only the active generation.
        (await ScalarLongAsync($"SELECT count(*) FROM {rollupTable}")).Should().Be(generationOneRows + 1);
        var readAfterInject = await repo.ExecuteQueryAsync(rollupRtId, StreamDataQueryOptions.Create()
            .WithCkTypeId(rollupSnapshot.TargetCkTypeId)
            .WithColumns(new List<string> { "voltage_sum" })
            .WithPagination(0, 1000));
        readAfterInject.Rows.Count.Should().Be((int)generationOneRows,
            "the genmap still points at generation 1, so the uncommitted generation-999 row must stay hidden (no mixed read)");

        // ── Recompute #2: writes generation 2, flips the pointer, sweeps generations 1 AND 999 ──
        var result2 = await executor.ExecuteAsync(
            sourceSnapshot, rollupSnapshot, RangeStart, RangeEnd, rtIdScope: null, CancellationToken.None);
        await RefreshAsync(rollupTable);
        await RefreshAsync(genMapTable);

        result2.WindowsProcessed.Should().Be(ExpectedBuckets);

        // Pointer advanced to generation 2 (monotonic), genmap still a single range row.
        (await ScalarLongAsync($"SELECT count(*) FROM {genMapTable}")).Should().Be(1);
        (await ScalarLongAsync($"SELECT \"generation\" FROM {genMapTable}")).Should().Be(2);

        // Sweep removed every superseded generation (1 and the injected 999) — only generation 2 left.
        (await DistinctGenerationsAsync(rollupTable)).Should().Equal(2L);
        (await ScalarLongAsync($"SELECT count(*) FROM {rollupTable}")).Should().Be(generationOneRows);

        // Read path still returns the same rows, now on generation 2.
        var read2 = await repo.ExecuteQueryAsync(rollupRtId, StreamDataQueryOptions.Create()
            .WithCkTypeId(rollupSnapshot.TargetCkTypeId)
            .WithColumns(new List<string> { "voltage_sum" })
            .WithPagination(0, 1000));
        read2.Rows.Count.Should().Be((int)generationOneRows);
    }

    /// <summary>
    /// AB#4188 — a rollup carrying several aggregations across <em>different</em> attributes
    /// (Sum(Voltage) + Max(Current) + First(Voltage) + Last(Current)) recomputes all of them in one
    /// pass and swaps the whole multi-column row atomically to the next generation — consumers never
    /// see a partial set. Also the end-to-end validation that the First/Last arg_min/arg_max SQL
    /// (CrateDB's <c>(MIN|MAX(ARRAY[time, value]))[2]</c> idiom) actually executes and populates its
    /// column against a real CrateDB, not just as a generated string.
    /// </summary>
    [Fact]
    public async Task Recompute_MultiAttributeRollup_PopulatesAllAggregatesAtomically_IncludingFirstLast()
    {
        fixture.OutputHelper = output;

        var systemContext = fixture.GetSystemContext();
        var tenantContext = await systemContext.FindTenantContextAsync(systemContext.TenantId);

        var rollupLifecycle = tenantContext.GetRollupArchiveLifecycleService()
            ?? throw new InvalidOperationException("Rollup lifecycle service not available.");
        var archiveLifecycle = tenantContext.GetArchiveLifecycleService()
            ?? throw new InvalidOperationException("Archive lifecycle service not available.");
        var archiveStore = tenantContext.GetArchiveRuntimeStore();
        var rollupStore = tenantContext.GetRollupArchiveRuntimeStore()
            ?? throw new InvalidOperationException("Rollup runtime store not available.");
        var repo = tenantContext.GetStreamDataRepository()
            ?? throw new InvalidOperationException("StreamData repository not available.");
        var executor = (IArchiveRecomputeExecutor)repo;

        // Four aggregations over two different source attributes, including First/Last (AB#4188).
        var rollupRtId = await rollupLifecycle.CreateAsync(
            rtWellKnownName: "MultiAttrFirstLastRollup",
            sourceArchiveRtId: fixture.ArchiveRtId,
            bucketSize: BucketSize,
            watermarkLag: TimeSpan.Zero,
            aggregations: new[]
            {
                new CkRollupAggregationSpec("Voltage", CkRollupFunction.Sum, null),
                new CkRollupAggregationSpec("Current", CkRollupFunction.Max, null),
                new CkRollupAggregationSpec("Voltage", CkRollupFunction.First, null),
                new CkRollupAggregationSpec("Current", CkRollupFunction.Last, null),
            });
        await archiveLifecycle.ActivateAsync(rollupRtId);

        var sourceSnapshot = await archiveStore.GetAsync(fixture.ArchiveRtId)
            ?? throw new InvalidOperationException("Source archive snapshot missing.");
        var rollupSnapshot = await rollupStore.GetAsync(rollupRtId)
            ?? throw new InvalidOperationException("Rollup archive snapshot missing.");

        var rollupTable = $"\"{fixture.StreamDataTenantId}\".\"archive_{rollupRtId}\"";
        var genMapTable = $"\"{fixture.StreamDataTenantId}\".\"archive_{rollupRtId}__genmap\"";

        // ── Recompute #1 → all four aggregate columns materialise under generation 1 ──
        var result1 = await executor.ExecuteAsync(
            sourceSnapshot, rollupSnapshot, RangeStart, RangeEnd, rtIdScope: null, CancellationToken.None);
        await RefreshAsync(rollupTable);
        // The genmap must be visible before the next recompute reads the active generation
        // (CrateDB applies writes to the read path asynchronously).
        await RefreshAsync(genMapTable);

        result1.WindowsProcessed.Should().Be(ExpectedBuckets);

        var totalRows = await ScalarLongAsync($"SELECT count(*) FROM {rollupTable} WHERE \"generation\" = 1");
        totalRows.Should().BeGreaterThan(0);

        // Every declared aggregate is populated on the SAME rows — one pass produced all four,
        // First/Last included (the arg SQL ran on CrateDB and wrote a value, not null).
        var fullyPopulated = await ScalarLongAsync(
            $"SELECT count(*) FROM {rollupTable} WHERE \"generation\" = 1 " +
            "AND \"voltage_sum\" IS NOT NULL AND \"current_max\" IS NOT NULL " +
            "AND \"voltage_first\" IS NOT NULL AND \"current_last\" IS NOT NULL");
        fullyPopulated.Should().Be(totalRows, "all four aggregates are produced together in one pass");

        // Each rtId has a single in-range point, so First(Voltage) == Sum(Voltage) and
        // Last(Current) == Max(Current) — cross-checks that the arg idiom returns the actual value.
        (await ScalarLongAsync(
            $"SELECT count(*) FROM {rollupTable} WHERE \"generation\" = 1 AND \"voltage_first\" != \"voltage_sum\""))
            .Should().Be(0, "First(Voltage) must equal the single point's Voltage");
        (await ScalarLongAsync(
            $"SELECT count(*) FROM {rollupTable} WHERE \"generation\" = 1 AND \"current_last\" != \"current_max\""))
            .Should().Be(0, "Last(Current) must equal the single point's Current");

        // ── Recompute #2 → the whole multi-column row swaps to generation 2 atomically ──
        var result2 = await executor.ExecuteAsync(
            sourceSnapshot, rollupSnapshot, RangeStart, RangeEnd, rtIdScope: null, CancellationToken.None);
        await RefreshAsync(rollupTable);
        await RefreshAsync(genMapTable);

        result2.WindowsProcessed.Should().Be(ExpectedBuckets);

        // No generation older than 2 survives, and every column is still populated together — a
        // consumer reading the active generation never observes a partial aggregate set.
        (await DistinctGenerationsAsync(rollupTable)).Should().Equal(2L);
        (await ScalarLongAsync(
            $"SELECT count(*) FROM {rollupTable} WHERE \"generation\" = 2 " +
            "AND \"voltage_sum\" IS NOT NULL AND \"current_max\" IS NOT NULL " +
            "AND \"voltage_first\" IS NOT NULL AND \"current_last\" IS NOT NULL"))
            .Should().Be(totalRows, "the atomic swap moved all four aggregate columns to generation 2 together");
    }

    // ── CrateDB helpers (direct npgsql, mirrors StreamDataFixture.RefreshArchiveTableAsync) ──

    private async Task RefreshAsync(string qualifiedTable)
    {
        await using var conn = new NpgsqlConnection(fixture.CrateDbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"REFRESH TABLE {qualifiedTable}", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(fixture.CrateDbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> ScalarLongAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(fixture.CrateDbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        var value = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(value, global::System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<List<long>> DistinctGenerationsAsync(string qualifiedTable)
    {
        await using var conn = new NpgsqlConnection(fixture.CrateDbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            $"SELECT DISTINCT \"generation\" FROM {qualifiedTable} ORDER BY \"generation\"", conn);
        var generations = new List<long>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            generations.Add(Convert.ToInt64(reader.GetValue(0), global::System.Globalization.CultureInfo.InvariantCulture));
        }
        return generations;
    }
}
