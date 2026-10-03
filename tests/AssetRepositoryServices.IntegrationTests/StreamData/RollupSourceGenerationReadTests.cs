using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
/// A rollup level that is built from another rollup level reads it in its active generation only.
/// The ladder here is raw → hourly → daily, several series, against a real CrateDB: the level above
/// must equal the aggregate of the level below whenever the lower level is recomputed — right after
/// it, with no pause in between, and while a recompute of it is in flight (its next generation
/// already copied in, the pointer not yet flipped).
/// </summary>
/// <remarks>
/// None of these tests refreshes a rollup or its generation map between the recompute of one level
/// and the aggregation of the next. Doing so would hide exactly what is under test: every statement
/// the engine relies on must be visible by the engine's own doing.
/// </remarks>
[Collection(StreamDataMutatingCollection.Name)]
public class RollupSourceGenerationReadTests(StreamDataFixture fixture, ITestOutputHelper output)
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);
    private const int SeriesCount = 5;

    /// <summary>
    /// The correction cascade: the raw values of a closed day change, the hourly level is
    /// recomputed and the daily level directly after it. Repeated, because the failure this pins
    /// depends on how far CrateDB has got with applying the hourly sweep when the daily level reads.
    /// </summary>
    [Fact]
    public async Task DailyRecompute_DirectlyAfterHourlyRecompute_EqualsTheHourlySum()
    {
        fixture.OutputHelper = output;
        var builder = new MultiSourceArchiveBuilder(fixture);
        var day = new DateTime(2026, 2, 3, 0, 0, 0, DateTimeKind.Utc);
        var ladder = await CreateLadderAsync(builder, "GenReadCascade", day, value: 1d);
        var orchestrator = (await builder.TenantAsync()).GetRecomputeOrchestrator()!;

        for (var round = 1; round <= 6; round++)
        {
            // The correction: every hour of every series now carries a different value.
            var value = 1d + round;
            foreach (var series in ladder.Series)
            {
                await builder.InsertPointsAsync(ladder.Raw, series, HourlyPoints(day, value));
            }

            var hourly = await orchestrator.RecomputeArchiveAsync(
                ladder.Hourly, day, day + Day, null, RecomputeTrigger.Manual, CancellationToken.None);
            var daily = await orchestrator.RecomputeArchiveAsync(
                ladder.Daily, day, day + Day, null, RecomputeTrigger.Manual, CancellationToken.None);

            hourly.State.Should().Be(RecomputeJobState.Completed);
            daily.State.Should().Be(RecomputeJobState.Completed);

            var dailyValues = await ActiveDailyValuesAsync(builder, ladder, day);
            dailyValues.Should().HaveCount(SeriesCount, $"round {round}: one daily row per series");
            dailyValues.Values.Should().AllSatisfy(
                v => v.Should().BeApproximately(24 * value, 1e-9,
                    $"round {round}: the day is the sum of its 24 hours at {value}, counted once"));
        }
    }

    /// <summary>
    /// A recompute of the hourly level is in flight: its next generation is already in the table,
    /// the pointer still names the previous one. Whatever aggregates the day at that moment — the
    /// recompute executor or the forward aggregation — must read the committed generation only.
    /// </summary>
    [Fact]
    public async Task DailyAggregation_WhileAnHourlyGenerationIsStagedButNotCommitted_ReadsTheCommittedOne()
    {
        fixture.OutputHelper = output;
        var builder = new MultiSourceArchiveBuilder(fixture);
        var day = new DateTime(2026, 2, 5, 0, 0, 0, DateTimeKind.Utc);
        var ladder = await CreateLadderAsync(builder, "GenReadStaged", day, value: 2d);
        var hourlyTable = builder.QualifiedTable(ladder.Hourly);

        // The uncommitted generation: a full copy of the day under a generation no pointer names.
        await builder.ExecuteSqlAsync(
            $"INSERT INTO {hourlyTable} (\"window_start\",\"window_end\",\"rtid\",\"cktypeid\"," +
            $"\"rtwellknownname\",\"was_updated\",\"{MultiSourceArchiveBuilder.RollupColumn}\",\"generation\") " +
            $"SELECT \"window_start\",\"window_end\",\"rtid\",\"cktypeid\",\"rtwellknownname\",\"was_updated\"," +
            $"\"{MultiSourceArchiveBuilder.RollupColumn}\" * 10,999 FROM {hourlyTable}");
        await builder.RefreshAsync(ladder.Hourly);
        (await builder.ScalarLongAsync($"SELECT count(*) FROM {hourlyTable}"))
            .Should().Be(2 * 24 * SeriesCount, "both generations of the day are in the hourly table");

        // Recompute path.
        var job = await builder.RecomputeAsync(ladder.Daily, day, day + Day);
        job.State.Should().Be(RecomputeJobState.Completed);
        (await ActiveDailyValuesAsync(builder, ladder, day)).Values.Should().AllSatisfy(
            v => v.Should().BeApproximately(48d, 1e-9, "24 hours at 2, the uncommitted generation left out"));

        // Forward path: the same bucket aggregated the way the forward orchestrator does it, onto
        // the generation-0 row.
        var tenant = await builder.TenantAsync();
        var repo = tenant.GetStreamDataRepository()!;
        var hourlySnapshot = (await tenant.GetArchiveRuntimeStore().GetAsync(ladder.Hourly))!;
        var dailySnapshot = await builder.LoadRollupAsync(ladder.Daily);
        await repo.AggregateBucketAsync(hourlySnapshot, dailySnapshot, day, day + Day, CancellationToken.None);
        await builder.RefreshAsync(ladder.Daily);

        var forward = await builder.RowsAsync(
            $"SELECT \"{MultiSourceArchiveBuilder.RollupColumn}\" AS v FROM {builder.QualifiedTable(ladder.Daily)} " +
            $"WHERE \"generation\" = 0 AND \"window_start\"::bigint = {ToEpochMs(day)}");
        forward.Should().HaveCount(SeriesCount);
        forward.Select(r => Convert.ToDouble(r["v"], global::System.Globalization.CultureInfo.InvariantCulture))
            .Should().AllSatisfy(v => v.Should().BeApproximately(48d, 1e-9));
    }

    /// <summary>
    /// A rewind that cuts through a recomputed range keeps the rows before the cut on their
    /// generation, with a pointer of their own — so the level above still finds them.
    /// </summary>
    [Fact]
    public async Task Rewind_ThroughARecomputedRange_KeepsTheEarlierRowsReadable_ForReadersAndTheLevelAbove()
    {
        fixture.OutputHelper = output;
        var builder = new MultiSourceArchiveBuilder(fixture);
        var day = new DateTime(2026, 2, 7, 0, 0, 0, DateTimeKind.Utc);
        var cut = day.AddHours(12);
        var ladder = await CreateLadderAsync(builder, "GenReadRewind", day, value: 3d);
        var tenant = await builder.TenantAsync();

        await tenant.GetStreamDataRepository()!.ClearRecomputeGenerationsAsync(
            ladder.Hourly, cut, TestContext.Current.CancellationToken);

        var pointers = await builder.RowsAsync(
            $"SELECT \"range_start\", \"range_end\" FROM {builder.GenMapTable(ladder.Hourly)}");
        pointers.Should().ContainSingle("the part of the pointer before the cut is kept, the rest is gone");
        Convert.ToInt64(pointers[0]["range_start"]).Should().Be(ToEpochMs(day));
        Convert.ToInt64(pointers[0]["range_end"]).Should().Be(ToEpochMs(cut));

        // The level above aggregates the day from what is left of the hourly level: the 12 hours
        // before the cut. Without a pointer for them it would find nothing at all.
        var job = await builder.RecomputeAsync(ladder.Daily, day, day + Day);
        job.State.Should().Be(RecomputeJobState.Completed);
        (await ActiveDailyValuesAsync(builder, ladder, day)).Values.Should().AllSatisfy(
            v => v.Should().BeApproximately(36d, 1e-9, "12 hours at 3 remain before the cut"));
    }

    // ── helpers ──

    private sealed record Ladder(
        OctoObjectId Raw, OctoObjectId Hourly, OctoObjectId Daily, IReadOnlyList<OctoObjectId> Series);

    /// <summary>
    /// raw → hourly → daily over one closed day, <see cref="SeriesCount"/> series with one value per
    /// hour, both levels recomputed once so each carries a generation pointer for the day.
    /// </summary>
    private static async Task<Ladder> CreateLadderAsync(
        MultiSourceArchiveBuilder builder, string name, DateTime day, double value)
    {
        var raw = await builder.CreateRawArchiveAsync($"{name}Raw");
        var series = Enumerable.Range(0, SeriesCount).Select(_ => OctoObjectId.GenerateNewId()).ToList();
        foreach (var s in series)
        {
            await builder.InsertPointsAsync(raw, s, HourlyPoints(day, value));
        }

        var hourly = await builder.CreateRollupAsync($"{name}Hourly", [new RollupSourceReference(raw)], Hour);
        await builder.ActivateAsync(hourly);
        var daily = await builder.CreateRollupAsync(
            $"{name}Daily", [new RollupSourceReference(hourly)], Day, MultiSourceArchiveBuilder.LogicalSum);
        await builder.ActivateAsync(daily);

        (await builder.RecomputeAsync(hourly, day, day + Day)).State.Should().Be(RecomputeJobState.Completed);
        (await builder.RecomputeAsync(daily, day, day + Day)).State.Should().Be(RecomputeJobState.Completed);
        return new Ladder(raw, hourly, daily, series);
    }

    private static IEnumerable<(DateTime Timestamp, double Value)> HourlyPoints(DateTime day, double value) =>
        Enumerable.Range(0, 24).Select(h => (day.AddHours(h).AddMinutes(30), value));

    /// <summary>The daily value per series in the generation the daily pointer names.</summary>
    private static async Task<IReadOnlyDictionary<string, double>> ActiveDailyValuesAsync(
        MultiSourceArchiveBuilder builder, Ladder ladder, DateTime day)
    {
        await builder.RefreshAsync(ladder.Daily);
        await builder.ExecuteSqlAsync($"REFRESH TABLE {builder.GenMapTable(ladder.Daily)}");
        var generation = await builder.ScalarLongAsync(
            $"SELECT MAX(\"generation\") FROM {builder.GenMapTable(ladder.Daily)}");
        var rows = await builder.RowsAsync(
            $"SELECT \"rtid\", \"{MultiSourceArchiveBuilder.RollupColumn}\" AS v FROM {builder.QualifiedTable(ladder.Daily)} " +
            $"WHERE \"generation\" = {generation} AND \"window_start\"::bigint = {ToEpochMs(day)}");
        return rows.ToDictionary(
            r => r["rtid"]!.ToString()!,
            r => Convert.ToDouble(r["v"], global::System.Globalization.CultureInfo.InvariantCulture));
    }

    private static long ToEpochMs(DateTime value) =>
        new DateTimeOffset(value, TimeSpan.Zero).ToUnixTimeMilliseconds();
}
