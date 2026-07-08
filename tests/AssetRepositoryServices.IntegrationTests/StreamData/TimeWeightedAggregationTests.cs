using System.Globalization;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Npgsql;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
/// AB#4336 / AB#4340 — end-to-end validation of the time-weighted (LOCF) aggregation SQL against a
/// REAL CrateDB (Testcontainer). Conserves the manual live validation on the energyiq
/// LuminaireArchive as CI coverage: every expected value below is a hand-computed interval sum, so
/// a CrateDB behaviour change (window functions, casts, upsert) surfaces as an exact-value diff.
/// </summary>
/// <remarks>
/// The synthetic event series (all UTC, 1h buckets on 2026-05-01, far outside the fixture's
/// canonical data so the default 35-day carry lookback cannot cross-contaminate):
///
/// <code>
/// rtId A:  11:15 voltage=100 status=2     11:45 voltage=0 status=1
/// rtId B:  11:00 voltage=100 status=1     11:30 voltage=NULL status=1
/// </code>
///
/// Expected per bucket (concept-time-weighted §3):
/// - [10:00, 11:00): no rows at all — no carry within lookback, no events.
/// - [11:00, 12:00) A: covered 11:15→12:00 = 2 700 000 ms, integral = 100×1 800 000 = 180 000 000.
/// - [11:00, 12:00) B: covered 11:00→11:30 = 1 800 000 ms (NULL terminates coverage),
///   integral = 180 000 000.
/// - [12:00, 13:00) A: pure carry (voltage 0) — full 3 600 000 ms covered, integral 0. The row
///   EXISTS although no event falls into the bucket ("light stays on across a silent bucket").
/// - [12:00, 13:00) B: carry is the NULL observation — row exists, integral/duration NULL.
/// - StateDuration(status == 2) A: [11:00, 12:00) = 1 800 000 ms; carry bucket = NULL (state 1).
/// - Plain MAX(voltage) rides along carry-guarded: NULL in the pure-carry bucket.
/// </remarks>
[Collection("Sequential")]
public class TimeWeightedAggregationTests(StreamDataFixture fixture, ITestOutputHelper output)
    : IClassFixture<StreamDataFixture>
{
    private static readonly DateTime B0Start = new(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime B1Start = new(2026, 5, 1, 11, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime B2Start = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime B2End = new(2026, 5, 1, 13, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan OneHour = TimeSpan.FromHours(1);

    private const double ExpectedIntegralA = 180_000_000d; // 100 × 30 min
    private const long ExpectedDurationA = 2_700_000L;     // 11:15 → 12:00
    private const double ExpectedIntegralB = 180_000_000d; // 100 × 30 min
    private const long ExpectedDurationB = 1_800_000L;     // 11:00 → 11:30 (NULL terminates)
    private const long ExpectedStateDurationA = 1_800_000L; // status 2 from 11:15 → 11:45

    [Fact]
    public async Task TwaAndStateDurationRollup_ForwardAggregation_MatchesHandComputedIntervalSums()
    {
        var setup = await CreateEventArchiveWithRollupAsync("TwaForward");

        // Aggregate the three buckets exactly like the orchestrator would (per closed bucket).
        var repo = setup.Repo;
        (await repo.AggregateBucketAsync(setup.SourceSnapshot, setup.RollupSnapshot, B0Start, B1Start, CancellationToken.None))
            .Should().Be(0, "no carry within lookback and no events before 11:00 — no phantom bucket");
        await repo.AggregateBucketAsync(setup.SourceSnapshot, setup.RollupSnapshot, B1Start, B2Start, CancellationToken.None);
        await repo.AggregateBucketAsync(setup.SourceSnapshot, setup.RollupSnapshot, B2Start, B2End, CancellationToken.None);
        await RefreshAsync(setup.RollupTable);

        // ---- [11:00, 12:00): event bucket, partial coverage ----
        var b1Ms = ToUnixMs(B1Start);
        (await ScalarAsync<double>($"SELECT \"voltage_twavg_integral\" FROM {setup.RollupTable} WHERE \"window_start\" = {b1Ms} AND \"rtid\" = '{setup.RtIdA}'"))
            .Should().Be(ExpectedIntegralA);
        (await ScalarAsync<long>($"SELECT \"voltage_twavg_duration\" FROM {setup.RollupTable} WHERE \"window_start\" = {b1Ms} AND \"rtid\" = '{setup.RtIdA}'"))
            .Should().Be(ExpectedDurationA);
        (await ScalarAsync<double>($"SELECT \"voltage_twavg_integral\" FROM {setup.RollupTable} WHERE \"window_start\" = {b1Ms} AND \"rtid\" = '{setup.RtIdB}'"))
            .Should().Be(ExpectedIntegralB);
        (await ScalarAsync<long>($"SELECT \"voltage_twavg_duration\" FROM {setup.RollupTable} WHERE \"window_start\" = {b1Ms} AND \"rtid\" = '{setup.RtIdB}'"))
            .Should().Be(ExpectedDurationB, "a NULL observation terminates coverage until the next non-NULL one");
        (await ScalarAsync<long>($"SELECT \"operatingstatus_stateduration\" FROM {setup.RollupTable} WHERE \"window_start\" = {b1Ms} AND \"rtid\" = '{setup.RtIdA}'"))
            .Should().Be(ExpectedStateDurationA);
        (await ScalarAsync<double>($"SELECT \"voltage_max\" FROM {setup.RollupTable} WHERE \"window_start\" = {b1Ms} AND \"rtid\" = '{setup.RtIdA}'"))
            .Should().Be(100d);

        // ---- [12:00, 13:00): pure-carry bucket ----
        var b2Ms = ToUnixMs(B2Start);
        (await ScalarAsync<double>($"SELECT \"voltage_twavg_integral\" FROM {setup.RollupTable} WHERE \"window_start\" = {b2Ms} AND \"rtid\" = '{setup.RtIdA}'"))
            .Should().Be(0d, "the carried value 0 holds the whole bucket");
        (await ScalarAsync<long>($"SELECT \"voltage_twavg_duration\" FROM {setup.RollupTable} WHERE \"window_start\" = {b2Ms} AND \"rtid\" = '{setup.RtIdA}'"))
            .Should().Be(3_600_000L, "a pure-carry bucket is fully covered");
        (await ScalarNullableAsync($"SELECT \"voltage_max\" FROM {setup.RollupTable} WHERE \"window_start\" = {b2Ms} AND \"rtid\" = '{setup.RtIdA}'"))
            .Should().BeNull("plain aggregations must not see the carry-in virtual row");
        (await ScalarNullableAsync($"SELECT \"operatingstatus_stateduration\" FROM {setup.RollupTable} WHERE \"window_start\" = {b2Ms} AND \"rtid\" = '{setup.RtIdA}'"))
            .Should().BeNull("the carried state is 1, never 2");
        (await ScalarNullableAsync($"SELECT \"voltage_twavg_duration\" FROM {setup.RollupTable} WHERE \"window_start\" = {b2Ms} AND \"rtid\" = '{setup.RtIdB}'"))
            .Should().BeNull("the carried observation is NULL — the signal is unknown for the whole bucket");

        // No bucket materialised before the first observation.
        (await ScalarAsync<long>($"SELECT count(*) FROM {setup.RollupTable} WHERE \"window_start\" = {ToUnixMs(B0Start)}"))
            .Should().Be(0);
    }

    [Fact]
    public async Task Recompute_ReproducesForwardAggregatedTimeWeightedValues()
    {
        var setup = await CreateEventArchiveWithRollupAsync("TwaRecompute");
        var repo = setup.Repo;
        await repo.AggregateBucketAsync(setup.SourceSnapshot, setup.RollupSnapshot, B1Start, B2Start, CancellationToken.None);
        await repo.AggregateBucketAsync(setup.SourceSnapshot, setup.RollupSnapshot, B2Start, B2End, CancellationToken.None);
        await RefreshAsync(setup.RollupTable);

        var executor = (IArchiveRecomputeExecutor)repo;
        var result = await executor.ExecuteAsync(
            setup.SourceSnapshot, setup.RollupSnapshot, B1Start, B2End, rtIdScope: null, CancellationToken.None);
        await RefreshAsync(setup.RollupTable);

        result.WindowsProcessed.Should().Be(2);

        // The recomputed generation must reproduce the forward values bit-identically — the carry
        // is a pure function of source data (decision D1), so staging output cannot drift.
        var b1Ms = ToUnixMs(B1Start);
        (await ScalarAsync<double>($"SELECT \"voltage_twavg_integral\" FROM {setup.RollupTable} WHERE \"window_start\" = {b1Ms} AND \"rtid\" = '{setup.RtIdA}' AND \"generation\" = 1"))
            .Should().Be(ExpectedIntegralA);
        (await ScalarAsync<long>($"SELECT \"voltage_twavg_duration\" FROM {setup.RollupTable} WHERE \"window_start\" = {b1Ms} AND \"rtid\" = '{setup.RtIdA}' AND \"generation\" = 1"))
            .Should().Be(ExpectedDurationA);
        (await ScalarAsync<long>($"SELECT \"operatingstatus_stateduration\" FROM {setup.RollupTable} WHERE \"window_start\" = {b1Ms} AND \"rtid\" = '{setup.RtIdA}' AND \"generation\" = 1"))
            .Should().Be(ExpectedStateDurationA);
        (await ScalarAsync<long>($"SELECT \"voltage_twavg_duration\" FROM {setup.RollupTable} WHERE \"window_start\" = {ToUnixMs(B2Start)} AND \"rtid\" = '{setup.RtIdA}' AND \"generation\" = 1"))
            .Should().Be(3_600_000L);
    }

    [Fact]
    public async Task RawGroupedTimeWeightedQuery_WithAndWithoutFieldFilter_MatchesHandComputation()
    {
        var setup = await CreateEventArchiveWithRollupAsync("TwaRawQuery");
        var repo = setup.Repo;

        // Whole window [10:00, 13:00): the last observation of each entity holds until the window
        // end (query-time LOCF, concept §6.2).
        //   A: 100×(11:15→11:45) + 0×(11:45→13:00) = 180 000 000 over 6 300 000 ms → 28.571428…
        //   B: 100×(11:00→11:30), NULL afterwards → 180 000 000 over 1 800 000 ms → 100.
        var options = StreamDataGroupedAggregationQueryOptions.Create()
            .WithCkTypeId(new RtCkId<CkTypeId>(fixture.TestCkTypeId))
            .WithGroupByColumns(new List<string> { "rtId" })
            .WithAggregationColumns(new List<AggregationColumn>
            {
                new("Voltage", AggregationFunction.TimeWeightedAverage),
            })
            .WithTimeRange(B0Start, B2End);

        var unfiltered = await repo.ExecuteGroupedAggregationQueryAsync(setup.SourceRtId, options);
        unfiltered.Rows.Should().HaveCount(2);
        TwaFor(unfiltered.Rows, setup.RtIdA).Should().BeApproximately(180_000_000d / 6_300_000d, 1e-9);
        TwaFor(unfiltered.Rows, setup.RtIdB).Should().BeApproximately(100d, 1e-9);

        // Field filter selects the EVENT SET (carry + window scans): status == 2 keeps only A's
        // 11:15 observation, which then holds until the window end → TWA exactly 100; B has no
        // matching events at all and disappears from the result.
        var filtered = await repo.ExecuteGroupedAggregationQueryAsync(setup.SourceRtId,
            StreamDataGroupedAggregationQueryOptions.Create()
                .WithCkTypeId(new RtCkId<CkTypeId>(fixture.TestCkTypeId))
                .WithGroupByColumns(new List<string> { "rtId" })
                .WithAggregationColumns(new List<AggregationColumn>
                {
                    new("Voltage", AggregationFunction.TimeWeightedAverage),
                })
                .WithTimeRange(B0Start, B2End)
                .WithFieldFilters(new List<FieldFilter>
                {
                    new("OperatingStatus", FieldFilterOperator.Equals, 2),
                }));
        filtered.Rows.Should().HaveCount(1);
        TwaFor(filtered.Rows, setup.RtIdA).Should().BeApproximately(100d, 1e-9);
    }

    [Fact]
    public async Task CascadeRollup_SumChainedTwaPair_RecombinesExactly()
    {
        var setup = await CreateEventArchiveWithRollupAsync("TwaCascade");
        var repo = setup.Repo;
        await repo.AggregateBucketAsync(setup.SourceSnapshot, setup.RollupSnapshot, B1Start, B2Start, CancellationToken.None);
        await repo.AggregateBucketAsync(setup.SourceSnapshot, setup.RollupSnapshot, B2Start, B2End, CancellationToken.None);
        await RefreshAsync(setup.RollupTable);

        // Daily rollup accumulating the hourly TWA pair via SUM specs on the physical columns —
        // the documented cascade pattern (concept §3 / AVG precedent).
        var tenantContext = await fixture.GetSystemContext().FindTenantContextAsync(fixture.GetSystemContext().TenantId);
        var rollupLifecycle = tenantContext.GetRollupArchiveLifecycleService()!;
        var archiveStore = tenantContext.GetArchiveRuntimeStore()!;
        var rollupStore = tenantContext.GetRollupArchiveRuntimeStore()!;

        var dailyRtId = await rollupLifecycle.CreateAsync(
            $"TwaCascadeDaily{Guid.NewGuid():N}",
            setup.RollupRtId,
            TimeSpan.FromDays(1),
            TimeSpan.Zero,
            new[]
            {
                new CkRollupAggregationSpec("voltage_twavg_integral", CkRollupFunction.Sum, "voltage_twavg_integral"),
                new CkRollupAggregationSpec("voltage_twavg_duration", CkRollupFunction.Sum, "voltage_twavg_duration"),
            });
        await tenantContext.GetArchiveLifecycleService()!.ActivateAsync(dailyRtId);

        var hourlyAsSource = await archiveStore.GetAsync(setup.RollupRtId);
        var dailySnapshot = await rollupStore.GetAsync(dailyRtId);
        var dayStart = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        await repo.AggregateBucketAsync(hourlyAsSource!, dailySnapshot!, dayStart, dayStart.AddDays(1), CancellationToken.None);
        await RefreshAsync($"\"{fixture.StreamDataTenantId}\".\"archive_{dailyRtId}\"");

        // Chain read on the daily rollup with target TWA: SUM(integral) / SUM(duration) over both
        // entities and both hourly buckets:
        //   integrals: A 180 000 000 + 0, B 180 000 000            = 360 000 000
        //   durations: A 2 700 000 + 3 600 000, B 1 800 000        =   8 100 000
        var read = await repo.ExecuteAggregationQueryAsync(dailyRtId,
            StreamDataAggregationQueryOptions.Create()
                .WithCkTypeId(dailySnapshot!.TargetCkTypeId)
                .WithAggregationColumns(new List<AggregationColumn>
                {
                    new("Voltage", AggregationFunction.TimeWeightedAverage),
                })
                .WithTimeRange(dayStart, dayStart.AddDays(1)));

        read.Rows.Should().HaveCount(1);
        var twa = Convert.ToDouble(read.Rows[0].Values["voltage_twavg"], CultureInfo.InvariantCulture);
        twa.Should().BeApproximately(360_000_000d / 8_100_000d, 1e-9);
    }

    // ---------------------------------------------------------------------------------------------

    private sealed record EventArchiveSetup(
        OctoObjectId SourceRtId,
        OctoObjectId RollupRtId,
        ArchiveSnapshot SourceSnapshot,
        RollupArchiveSnapshot RollupSnapshot,
        IStreamDataRepository Repo,
        string RollupTable,
        string RtIdA,
        string RtIdB);

    /// <summary>
    /// Provisions a dedicated raw archive (isolated from the fixture's canonical data), inserts
    /// the synthetic event series and creates + activates the hourly rollup carrying the three
    /// aggregations under test (TWA + StateDuration + carry-guarded MAX).
    /// </summary>
    private async Task<EventArchiveSetup> CreateEventArchiveWithRollupAsync(string namePrefix)
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetSystemTenantRepository();
        var archive = new RtRawArchive
        {
            RtWellKnownName = $"{namePrefix}Archive{Guid.NewGuid():N}",
            TargetCkTypeId = fixture.TestCkTypeId,
            Status = RtCkArchiveStatusEnum.Created,
            Columns = new AttributeRecordValueList<RtCkArchiveColumnRecord>
            {
                new() { Path = "Voltage", Indexed = true, Required = false },
                new() { Path = "OperatingStatus", Indexed = true, Required = false },
            },
        };
        using (var session = await tenantRepository.GetSessionAsync())
        {
            session.StartTransaction();
            await tenantRepository.InsertOneRtEntityAsync(session, archive);
            await session.CommitTransactionAsync();
        }

        var tenantContext = await systemContext.FindTenantContextAsync(systemContext.TenantId);
        var archiveLifecycle = tenantContext.GetArchiveLifecycleService()!;
        await archiveLifecycle.ActivateAsync(archive.RtId);

        var repo = tenantContext.GetStreamDataRepository()!;
        var ckTypeId = new RtCkId<CkTypeId>(fixture.TestCkTypeId);
        var rtIdA = OctoObjectId.GenerateNewId();
        var rtIdB = OctoObjectId.GenerateNewId();
        await repo.InsertAsync(archive.RtId, new List<StreamDataPoint>
        {
            Point(rtIdA, ckTypeId, B1Start.AddMinutes(15), voltage: 100d, status: 2),
            Point(rtIdA, ckTypeId, B1Start.AddMinutes(45), voltage: 0d, status: 1),
            Point(rtIdB, ckTypeId, B1Start, voltage: 100d, status: 1),
            Point(rtIdB, ckTypeId, B1Start.AddMinutes(30), voltage: null, status: 1),
        });
        await RefreshAsync($"\"{fixture.StreamDataTenantId}\".\"archive_{archive.RtId}\"");

        var rollupLifecycle = tenantContext.GetRollupArchiveLifecycleService()!;
        var rollupRtId = await rollupLifecycle.CreateAsync(
            $"{namePrefix}Rollup{Guid.NewGuid():N}",
            archive.RtId,
            OneHour,
            TimeSpan.Zero,
            new[]
            {
                new CkRollupAggregationSpec("Voltage", CkRollupFunction.TimeWeightedAvg, null),
                new CkRollupAggregationSpec("OperatingStatus", CkRollupFunction.StateDuration, null, "2"),
                new CkRollupAggregationSpec("Voltage", CkRollupFunction.Max, null),
            });
        await archiveLifecycle.ActivateAsync(rollupRtId);

        var archiveStore = tenantContext.GetArchiveRuntimeStore()!;
        var rollupStore = tenantContext.GetRollupArchiveRuntimeStore()!;
        var sourceSnapshot = (await archiveStore.GetAsync(archive.RtId))!;
        var rollupSnapshot = (await rollupStore.GetAsync(rollupRtId))!;

        output.WriteLine($"{namePrefix}: source={archive.RtId} rollup={rollupRtId} A={rtIdA} B={rtIdB}");

        return new EventArchiveSetup(
            archive.RtId, rollupRtId, sourceSnapshot, rollupSnapshot, repo,
            $"\"{fixture.StreamDataTenantId}\".\"archive_{rollupRtId}\"",
            rtIdA.ToString(), rtIdB.ToString());
    }

    private static StreamDataPoint Point(
        OctoObjectId rtId, RtCkId<CkTypeId> ckTypeId, DateTime timestamp, double? voltage, int status)
    {
        var attributes = new Dictionary<string, object?> { ["operatingStatus"] = status };
        if (voltage is not null)
        {
            attributes["voltage"] = voltage;
        }

        return new StreamDataPoint
        {
            RtId = rtId,
            CkTypeId = ckTypeId,
            Timestamp = timestamp,
            RtWellKnownName = $"Twa{rtId}",
            Attributes = attributes,
        };
    }

    private static double TwaFor(IReadOnlyList<StreamDataRow> rows, string rtId)
    {
        var row = rows.Single(r =>
            string.Equals(r.Values.TryGetValue("rtid", out var v) ? v?.ToString() : null, rtId, StringComparison.Ordinal));
        return Convert.ToDouble(row.Values["voltage_twavg"], CultureInfo.InvariantCulture);
    }

    private static long ToUnixMs(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeMilliseconds();

    private async Task RefreshAsync(string qualifiedTable)
    {
        await using var conn = new NpgsqlConnection(fixture.CrateDbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"REFRESH TABLE {qualifiedTable}", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        var value = await ScalarNullableAsync(sql);
        value.Should().NotBeNull($"query must yield a value: {sql}");
        return (T)Convert.ChangeType(value!, typeof(T), CultureInfo.InvariantCulture);
    }

    private async Task<object?> ScalarNullableAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(fixture.CrateDbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        var value = await cmd.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }
}
