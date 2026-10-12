using System.Globalization;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
/// AB#6283, against a REAL CrateDB: a rollup recompute copies and sweeps in row-counted slices (the
/// fixture cuts at <see cref="RowBoundStreamDataFixture.SliceTargetRows"/> rows, so a few hundred
/// rows are many statements) and the result is what one statement per step produced — the values of
/// the source, one pointer entry, one generation left. Also pinned here: rows an uncommitted copy
/// left under the next generation number are not adopted, and rows a failed sweep left below a
/// pointer are reclaimed by an executor that never saw the failure.
/// </summary>
[Collection(StreamDataRowBoundCollection.Name)]
public class RollupRecomputeRowBoundTests(RowBoundStreamDataFixture fixture, ITestOutputHelper output)
{
    private const int SeriesCount = 10;
    private const int Hours = 30;

    /// <summary>The physical columns of a rollup row, without its value and generation.</summary>
    private const string KeyColumns =
        "\"window_start\",\"window_end\",\"rtid\",\"cktypeid\",\"rtwellknownname\",\"was_updated\"";

    private static readonly DateTime Anchor = new(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly MultiSourceArchiveBuilder _builder = new(fixture);

    [Fact]
    public async Task ARangeOfManySlices_EqualsTheSourceSums_WithOnePointerEntry_AndOneGenerationLeft()
    {
        fixture.OutputHelper = output;
        var ladder = await BuildLadderAsync("RowBoundSlices");

        var job = await _builder.RecomputeAsync(ladder.Rollup, Anchor, Anchor.AddHours(Hours));

        job.State.Should().Be(RecomputeJobState.Completed, job.ErrorReason);
        job.WindowsProcessed.Should().Be(Hours);
        // 300 rows against a slice target of 20: the copy alone is at least eight statements.
        await AssertRollupEqualsSourceAsync(ladder, Hours, expectedGeneration: 1);
    }

    [Fact]
    public async Task ASecondRecompute_SweepsThePreviousGenerationCompletely()
    {
        fixture.OutputHelper = output;
        var ladder = await BuildLadderAsync("RowBoundSweep");
        (await _builder.RecomputeAsync(ladder.Rollup, Anchor, Anchor.AddHours(Hours))).State
            .Should().Be(RecomputeJobState.Completed);

        // The source loses its last ten hours: those windows are not staged again, and their rows of
        // the previous generation still have to go — the sweep covers the range, not the staged rows.
        const int remainingHours = Hours - 10;
        await _builder.ExecuteSqlAsync(
            $"DELETE FROM {_builder.QualifiedTable(ladder.Source)} " +
            $"WHERE \"timestamp\" >= {EpochMs(Anchor.AddHours(remainingHours))}");
        await _builder.RefreshAsync(ladder.Source);

        var job = await _builder.RecomputeAsync(ladder.Rollup, Anchor, Anchor.AddHours(Hours));

        job.State.Should().Be(RecomputeJobState.Completed, job.ErrorReason);
        await AssertRollupEqualsSourceAsync(ladder, remainingHours, expectedGeneration: 2);
    }

    [Fact]
    public async Task LeftoverRowsAtTheNextGeneration_AreNotAdopted()
    {
        fixture.OutputHelper = output;
        var ladder = await BuildLadderAsync("RowBoundLeftover");
        (await _builder.RecomputeAsync(ladder.Rollup, Anchor, Anchor.AddHours(Hours))).State
            .Should().Be(RecomputeJobState.Completed);

        // What a copy that was cut short before its flip leaves: rows under generation 2 — the
        // number the next recompute is handed, because the pointer table still ends at 1 — with a
        // value of their own for one series, and for a series the source does not have at all.
        // Without the leftover guard CrateDB skips the rows of the copy whose key already exists
        // (no error), so the recompute completes with 999 for that series and with the ghost series
        // visible (observed on main before AB#6283).
        var table = _builder.QualifiedTable(ladder.Rollup);
        var ghost = OctoObjectId.GenerateNewId();
        await _builder.ExecuteSqlAsync(
            $"INSERT INTO {table} ({KeyColumns},\"voltage_sum\",\"generation\") " +
            $"SELECT {KeyColumns},999.0,2 FROM {table} WHERE \"generation\" = 1 AND \"rtid\" = '{ladder.Series[0]}'");
        await _builder.ExecuteSqlAsync(
            $"INSERT INTO {table} ({KeyColumns},\"voltage_sum\",\"generation\") " +
            $"SELECT \"window_start\",\"window_end\",'{ghost}',\"cktypeid\",\"rtwellknownname\",\"was_updated\",5.0,2 " +
            $"FROM {table} WHERE \"generation\" = 1 AND \"rtid\" = '{ladder.Series[0]}'");
        await _builder.RefreshAsync(ladder.Rollup);

        var job = await _builder.RecomputeAsync(ladder.Rollup, Anchor, Anchor.AddHours(Hours));

        job.State.Should().Be(RecomputeJobState.Completed, job.ErrorReason);
        (await _builder.ReadBucketsAsync(ladder.Rollup, seriesRtId: ghost)).Should().BeEmpty(
            "a series the recompute does not produce must not become visible with its flip");
        await AssertRollupEqualsSourceAsync(ladder, Hours, expectedGeneration: 2);
    }

    [Fact]
    public async Task DeadGenerationsBelowThePointer_LeftByAFailedSweep_AreReclaimedByAFreshExecutor()
    {
        fixture.OutputHelper = output;
        var ladder = await BuildLadderAsync("RowBoundReclaim");
        var table = _builder.QualifiedTable(ladder.Rollup);
        var a = ladder.Series[0];
        var b = ladder.Series[1];
        var firstEnd = Anchor.AddHours(10);
        var secondStart = Anchor.AddHours(20);
        var secondEnd = Anchor.AddHours(30);

        // Hours 0-10 on generation 2 (recomputed twice), hours 20-30 on generation 3, and series a
        // there on generation 4 through a recompute scoped to it.
        (await _builder.RecomputeAsync(ladder.Rollup, Anchor, firstEnd)).State.Should().Be(RecomputeJobState.Completed);
        (await _builder.RecomputeAsync(ladder.Rollup, Anchor, firstEnd)).State.Should().Be(RecomputeJobState.Completed);
        (await _builder.RecomputeAsync(ladder.Rollup, secondStart, secondEnd)).State.Should().Be(RecomputeJobState.Completed);
        (await _builder.RecomputeAsync(ladder.Rollup, secondStart, secondEnd, a)).State.Should().Be(RecomputeJobState.Completed);
        (await GenerationsAsync(table, Anchor, firstEnd)).Should().Equal(2L);
        (await GenerationsAsync(table, secondStart, secondEnd)).Should().Equal(3L, 4L);

        // The state a killed sweep leaves, built by hand — in ranges no recompute will touch again:
        // rows of generation 1 below the pointer at 2 …
        await CopyRowsAsync(table, fromGeneration: 2, toGeneration: 1, Anchor, firstEnd);
        // … and rows of series a at generation 3 below its scoped pointer at 4.
        await CopyRowsAsync(table, fromGeneration: 4, toGeneration: 3, secondStart, secondEnd, a);
        // What must survive: rows of a newer generation than the pointer (a recompute that
        // overlaps the range and has not flipped yet) …
        await CopyRowsAsync(table, fromGeneration: 2, toGeneration: 9, Anchor, firstEnd);
        // … and generation-0 rows outside every pointer range (the forward aggregation's lane).
        await _builder.ExecuteSqlAsync(
            $"INSERT INTO {table} ({KeyColumns},\"voltage_sum\",\"generation\") " +
            $"SELECT {EpochMs(Anchor.AddHours(15))},{EpochMs(Anchor.AddHours(16))},\"rtid\",\"cktypeid\",\"rtwellknownname\",\"was_updated\",\"voltage_sum\",0 " +
            $"FROM {table} WHERE \"generation\" = 2 AND \"window_start\" = {EpochMs(Anchor)}");
        await _builder.RefreshAsync(ladder.Rollup);
        var deadBefore = await CountAsync(table, 1, Anchor, firstEnd) + await CountAsync(table, 3, secondStart, secondEnd, a);
        deadBefore.Should().Be(10 * SeriesCount + 10);
        // The rows of the OTHER series at generation 3 in the second range are live: the pointer
        // at 4 is scoped to series a, and their pointer is the unscoped one at 3.
        var liveOfOtherSeries = await CountAsync(table, 3, secondStart, secondEnd, b);
        liveOfOtherSeries.Should().Be(10);

        // A fresh executor — what a restarted service is — recomputes a range that has nothing to
        // do with any of this.
        var tenant = await _builder.TenantAsync();
        var repository = fixture.GetService<IStreamDataRepositoryFactory>().Create(
            tenant.TenantId, tenant.GetArchiveRuntimeStore(), tenant.GetRollupArchiveRuntimeStore(),
            tenant.GetArchiveRecomputeStateStore());
        var sourceSnapshot = (await tenant.GetArchiveRuntimeStore().GetAsync(ladder.Source))!;
        var rollupSnapshot = await _builder.LoadRollupAsync(ladder.Rollup);
        await ((IArchiveRecomputeExecutor)repository).ExecuteAsync(
            sourceSnapshot, rollupSnapshot, Anchor.AddHours(40), Anchor.AddHours(42), null,
            TestContext.Current.CancellationToken);
        await _builder.RefreshAsync(ladder.Rollup);

        (await CountAsync(table, 1, Anchor, firstEnd)).Should().Be(0, "rows below the pointer of their range are dead");
        (await CountAsync(table, 3, secondStart, secondEnd, a)).Should().Be(0, "rows below a scoped pointer are dead for its series");
        (await CountAsync(table, 2, Anchor, firstEnd)).Should().Be(10 * SeriesCount, "the active generation stays");
        (await CountAsync(table, 9, Anchor, firstEnd)).Should().Be(10 * SeriesCount, "a newer generation is not below the pointer");
        (await CountAsync(table, 3, secondStart, secondEnd, b)).Should().Be(liveOfOtherSeries, "a scoped pointer says nothing about other series");
        (await CountAsync(table, 4, secondStart, secondEnd, a)).Should().Be(10);
        (await CountAsync(table, 0, Anchor.AddHours(15), Anchor.AddHours(16))).Should().Be(SeriesCount,
            "generation 0 outside every pointer range is the forward aggregation's lane");

        // And readers still get every value.
        var buckets = await _builder.ReadBucketsAsync(ladder.Rollup, seriesRtId: b);
        buckets.Where(x => x.WindowStart < firstEnd && x.WindowStart >= Anchor)
            .Select(x => x.Value).Distinct().Should().Equal(ExpectedSum(1));
    }

    // ── ladder + helpers ──────────────────────────────────────────────────────────────────────

    private sealed record Ladder(OctoObjectId Source, OctoObjectId Rollup, IReadOnlyList<OctoObjectId> Series);

    /// <summary>
    /// A raw archive with <see cref="SeriesCount"/> series over <see cref="Hours"/> hours — two
    /// points an hour, valued by the series' index — and an hourly SUM rollup on it. Every (series,
    /// hour) is one rollup row whose value identifies the series.
    /// </summary>
    private async Task<Ladder> BuildLadderAsync(string namePrefix)
    {
        var source = await _builder.CreateRawArchiveAsync($"{namePrefix}Source");
        var series = Enumerable.Range(0, SeriesCount).Select(_ => OctoObjectId.GenerateNewId()).ToList();
        for (var i = 0; i < series.Count; i++)
        {
            var value = i + 1d;
            await _builder.InsertPointsAsync(source, series[i],
                Enumerable.Range(0, Hours).SelectMany(h => new[]
                {
                    (Anchor.AddHours(h).AddMinutes(10), value),
                    (Anchor.AddHours(h).AddMinutes(40), value),
                }));
        }

        var rollup = await _builder.CreateRollupAsync(
            $"{namePrefix}Rollup", [new RollupSourceReference(source)], TimeSpan.FromHours(1));
        await _builder.ActivateAsync(rollup);
        return new Ladder(source, rollup, series);
    }

    private static double ExpectedSum(int seriesIndex) => 2 * (seriesIndex + 1d);

    /// <summary>
    /// The rollup holds exactly one row per series and hour of the first <paramref name="hours"/>
    /// hours, each with the sum of its two source points, all on one generation, under one pointer
    /// entry for the recomputed range.
    /// </summary>
    private async Task AssertRollupEqualsSourceAsync(Ladder ladder, int hours, long expectedGeneration)
    {
        var table = _builder.QualifiedTable(ladder.Rollup);
        await _builder.RefreshAsync(ladder.Rollup);

        (await _builder.ScalarLongAsync($"SELECT count(*) FROM {table}")).Should().Be((long)SeriesCount * hours,
            "nothing is lost, doubled or left behind by cutting the copy and the sweep into slices");
        (await GenerationsAsync(table, Anchor, Anchor.AddHours(Hours))).Should().Equal(expectedGeneration);

        var pointers = await _builder.RowsAsync(
            $"SELECT \"range_start\", \"range_end\", \"rtid_scope\", \"generation\" FROM {_builder.GenMapTable(ladder.Rollup)}");
        var pointer = pointers.Should().ContainSingle("the slices are statements, not commits: one flip for the range").Subject;
        AsLong(pointer["range_start"]).Should().Be(long.Parse(EpochMs(Anchor), CultureInfo.InvariantCulture));
        AsLong(pointer["range_end"]).Should().Be(long.Parse(EpochMs(Anchor.AddHours(Hours)), CultureInfo.InvariantCulture));
        AsLong(pointer["generation"]).Should().Be(expectedGeneration);

        for (var i = 0; i < ladder.Series.Count; i++)
        {
            var buckets = await _builder.ReadBucketsAsync(ladder.Rollup, seriesRtId: ladder.Series[i]);
            buckets.Select(x => x.WindowStart).Should().Equal(
                Enumerable.Range(0, hours).Select(h => Anchor.AddHours(h)), "every hour once, in series {0}", i);
            buckets.Select(x => x.Value).Distinct().Should().Equal(ExpectedSum(i));
        }
    }

    /// <summary>Copies the rows of one generation in a range to another generation number.</summary>
    private async Task CopyRowsAsync(
        string table, long fromGeneration, long toGeneration, DateTime from, DateTime to, OctoObjectId? series = null)
    {
        var scope = series is null ? string.Empty : $" AND \"rtid\" = '{series}'";
        await _builder.ExecuteSqlAsync(
            $"INSERT INTO {table} ({KeyColumns},\"voltage_sum\",\"generation\") " +
            $"SELECT {KeyColumns},\"voltage_sum\",{toGeneration.ToString(CultureInfo.InvariantCulture)} FROM {table} " +
            $"WHERE \"generation\" = {fromGeneration.ToString(CultureInfo.InvariantCulture)} " +
            $"AND \"window_start\" >= {EpochMs(from)} AND \"window_start\" < {EpochMs(to)}{scope}");
        await _builder.ExecuteSqlAsync($"REFRESH TABLE {table}");
    }

    private async Task<long> CountAsync(
        string table, long generation, DateTime from, DateTime to, OctoObjectId? series = null)
    {
        var scope = series is null ? string.Empty : $" AND \"rtid\" = '{series}'";
        return await _builder.ScalarLongAsync(
            $"SELECT count(*) FROM {table} WHERE \"generation\" = {generation.ToString(CultureInfo.InvariantCulture)} " +
            $"AND \"window_start\" >= {EpochMs(from)} AND \"window_start\" < {EpochMs(to)}{scope}");
    }

    private async Task<IReadOnlyList<long>> GenerationsAsync(string table, DateTime from, DateTime to)
    {
        var rows = await _builder.RowsAsync(
            $"SELECT DISTINCT \"generation\" FROM {table} " +
            $"WHERE \"window_start\" >= {EpochMs(from)} AND \"window_start\" < {EpochMs(to)} ORDER BY \"generation\"");
        return rows.Select(r => AsLong(r["generation"])).ToList();
    }

    private static string EpochMs(DateTime value) =>
        new DateTimeOffset(value, TimeSpan.Zero).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

    private static long AsLong(object? value) => Convert.ToInt64(value, CultureInfo.InvariantCulture);
}
