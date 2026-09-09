using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
/// AB#5157 — retroactive corrections on a multi-source rollup, end to end against a real CrateDB.
/// <para>
/// A late write into a source archive is only a <em>correction</em> for the part of the timeline
/// that source is authoritative for: the consumed watermark of a source is the dependent's watermark
/// clipped to that source's validity span, the dirty window is clipped to the same span before it
/// becomes a pending recompute range, and the recompute itself is split into one segment per source
/// so no segment ever mixes two sources. These tests drive exactly that path — the production ingest
/// detector, <see cref="IRecomputeOrchestrator.PropagateDirtyWindowsAsync"/> and the background
/// drain <see cref="IRecomputeOrchestrator.TickAsync"/> — and read the result back out of CrateDB.
/// </para>
/// <para>
/// Every ladder is anchored a few hours before <c>UtcNow</c>: the forward orchestrator only commits
/// CLOSED buckets and seeds its watermark from the clock, so a synthetic history in the far past
/// could never be "before the watermark" the way a real late value is.
/// </para>
/// <para>
/// Most corrections here write TWO late points at distinct timestamps, which is the shape whose
/// dirty window survives Mongo's millisecond resolution unchanged. The degenerate single-timestamp
/// shape gets its own fact —
/// <see cref="ASingleTimestampCorrection_SchedulesExactlyOneBucketRecomputeOnTheDependent"/>.
/// </para>
/// </summary>
[Collection(StreamDataMutatingCollection.Name)]
public class RollupMultiSourceRecomputeTests(StreamDataFixture fixture, ITestOutputHelper output)
{
    private readonly MultiSourceArchiveBuilder _builder = new(fixture);

    private static readonly TimeSpan OneHour = TimeSpan.FromHours(1);

    /// <summary>Value of every point the ladder seeds into the legacy source.</summary>
    private const double LegacyValue = 1d;

    /// <summary>Value of every point the ladder seeds into the native source.</summary>
    private const double NativeValue = 3d;

    /// <summary>Value of each of the two late points a correction writes.</summary>
    private const double CorrectionValue = 5d;

    /// <summary>What a correction adds to the bucket it lands in.</summary>
    private const double CorrectionTotal = 2 * CorrectionValue;

    private const double Tolerance = 1e-9;

    [Fact]
    public async Task TC_REC_03_TC_REC_05_ALateWriteIntoTheLegacySource_RecomputesOnlyItsOwnSpan()
    {
        fixture.OutputHelper = output;
        var ladder = await BuildLadderAsync("RecLegacy");
        var correctedBucket = ladder.Anchor.AddHours(1);

        // Late values inside the legacy span, well before the consumed watermark.
        await WriteLateValuesAsync(ladder.Legacy, ladder.SeriesX, correctedBucket);

        var windows = await DirtyWindowsAsync(ladder.Legacy);
        windows.Should().ContainSingle("the write is retroactive for the span the legacy source owns");
        windows[0].ChangeKind.Should().Be(RecomputeChangeKind.RetroactiveModify);
        windows[0].WindowStart.Should().Be(correctedBucket.AddMinutes(30));

        await DrainAsync();

        var jobs = await CompletedJobsAsync(ladder.Rollup);
        jobs.Should().ContainSingle("one dirty window becomes one recompute of the affected bucket");
        jobs[0].RangeStart.Should().Be(correctedBucket);
        jobs[0].RangeEnd.Should().Be(correctedBucket.AddHours(1));
        jobs[0].RangeEnd.Should().BeOnOrBefore(ladder.Cutover,
            "a change in the legacy source can never make a bucket the native source serves stale");

        var buckets = await BucketsAsync(ladder.Rollup, ladder.SeriesX);
        buckets[correctedBucket].Should().BeApproximately(LegacyValue + CorrectionTotal, Tolerance);
        buckets[ladder.Anchor].Should().BeApproximately(LegacyValue, Tolerance, "unaffected bucket");
        buckets[ladder.Cutover].Should().BeApproximately(NativeValue, Tolerance,
            "the native side of the cutover is untouched");

        // The automatic path recomputes the whole range, not one entity (it enqueues with no rtId
        // scope), so a second series in the same bucket is re-derived — identically.
        (await BucketsAsync(ladder.Rollup, ladder.SeriesY))[correctedBucket]
            .Should().BeApproximately(LegacyValue, Tolerance,
                "only the series the late values belong to changes value");
    }

    [Fact]
    public async Task TC_REC_04_ALateWriteIntoTheNativeSource_RecomputesOnlyItsOwnSpan()
    {
        fixture.OutputHelper = output;
        var ladder = await BuildLadderAsync("RecNative");
        var correctedBucket = ladder.Cutover.AddHours(1);

        await WriteLateValuesAsync(ladder.Native, ladder.SeriesX, correctedBucket);

        (await DirtyWindowsAsync(ladder.Native)).Should().ContainSingle(
            "the second source triggers a recompute exactly like the first");

        await DrainAsync();

        var jobs = await CompletedJobsAsync(ladder.Rollup);
        jobs.Should().ContainSingle();
        jobs[0].RangeStart.Should().Be(correctedBucket);
        jobs[0].RangeStart.Should().BeOnOrAfter(ladder.Cutover,
            "a change in the native source cannot make a legacy bucket stale");

        var buckets = await BucketsAsync(ladder.Rollup, ladder.SeriesX);
        buckets[correctedBucket].Should().BeApproximately(NativeValue + CorrectionTotal, Tolerance);
        buckets[ladder.Cutover].Should().BeApproximately(NativeValue, Tolerance, "unaffected bucket");
        buckets[ladder.Anchor].Should().BeApproximately(LegacyValue, Tolerance,
            "the legacy side of the cutover is untouched");
    }

    [Fact]
    public async Task TC_REC_06_AWriteIntoASourceOutsideItsOwnSpan_SchedulesNothing()
    {
        fixture.OutputHelper = output;
        var ladder = await BuildLadderAsync("RecOutOfSpan");
        var nativeBucket = ladder.Cutover.AddHours(1);
        var before = await BucketsAsync(ladder.Rollup, ladder.SeriesX);

        // The legacy archive is written past its ValidTo. Its consumed watermark is clipped to the
        // span, so this is a FORWARD write for that source — nothing about the rollup is stale.
        await WriteLateValuesAsync(ladder.Legacy, ladder.SeriesX, nativeBucket);

        (await DirtyWindowsAsync(ladder.Legacy)).Should().BeEmpty(
            "a write outside the span the source is authoritative for is not a correction");

        await (await OrchestratorAsync()).PropagateDirtyWindowsAsync(ladder.Legacy, CancellationToken.None);
        (await PendingRangesAsync(ladder.Rollup)).Should().BeEmpty("nothing was propagated");
        (await CompletedJobsAsync(ladder.Rollup)).Should().BeEmpty("no recompute ran");

        (await BucketsAsync(ladder.Rollup, ladder.SeriesX))[nativeBucket]
            .Should().BeApproximately(before[nativeBucket], Tolerance,
                "the bucket keeps the value the native source produced");
    }

    [Fact]
    public async Task AWriteExactlyAtTheCutover_BelongsToTheNativeSource_AndOnlyItSchedulesARecompute()
    {
        fixture.OutputHelper = output;
        var ladder = await BuildLadderAsync("RecBoundary");

        // ValidTo is exclusive: for the legacy source the cutover is already at its consumed
        // watermark, so a write AT the cutover is forward, not retroactive.
        await WriteLateValuesAsync(ladder.Legacy, ladder.SeriesX, ladder.Cutover, offsetMinutes: 0);
        (await DirtyWindowsAsync(ladder.Legacy)).Should().BeEmpty(
            "the bucket starting at the cutover is not the legacy source's any more");

        // ValidFrom is inclusive: the same instant IS inside the native span and before its consumed
        // watermark, so the native source's write is a correction.
        await WriteLateValuesAsync(ladder.Native, ladder.SeriesZ, ladder.Cutover, offsetMinutes: 0);
        (await DirtyWindowsAsync(ladder.Native)).Should().ContainSingle();

        await DrainAsync();

        var jobs = await CompletedJobsAsync(ladder.Rollup);
        jobs.Should().ContainSingle();
        jobs[0].RangeStart.Should().Be(ladder.Cutover);

        var newSeries = await BucketsAsync(ladder.Rollup, ladder.SeriesZ);
        newSeries.Should().ContainKey(ladder.Cutover,
            "the recompute materialised the bucket at the cutover from the native source");
        newSeries[ladder.Cutover].Should().BeApproximately(CorrectionTotal, Tolerance);
        newSeries.Keys.Should().NotContain(ladder.Anchor,
            "the series exists only from the cutover on — no legacy bucket is invented for it");
    }

    [Fact]
    public async Task SimultaneousCorrectionsOnBothSidesOfTheCutover_CoalesceIntoOneRecompute()
    {
        fixture.OutputHelper = output;
        var ladder = await BuildLadderAsync("RecCoalesce");
        var lastLegacyBucket = ladder.Cutover.AddHours(-1);
        var firstNativeBucket = ladder.Cutover;

        await WriteLateValuesAsync(ladder.Legacy, ladder.SeriesX, lastLegacyBucket);
        await WriteLateValuesAsync(ladder.Native, ladder.SeriesX, firstNativeBucket);

        (await DirtyWindowsAsync(ladder.Legacy)).Should().ContainSingle();
        (await DirtyWindowsAsync(ladder.Native)).Should().ContainSingle();

        await DrainAsync();

        var jobs = await CompletedJobsAsync(ladder.Rollup);
        jobs.Should().ContainSingle(
            "the two adjacent stale ranges merge into ONE recompute spanning the cutover — which the " +
            "executor then runs as one segment per source");
        jobs[0].RangeStart.Should().Be(lastLegacyBucket);
        jobs[0].RangeEnd.Should().Be(firstNativeBucket.AddHours(1));
        jobs[0].WindowsProcessed.Should().Be(2, "one bucket per side of the cutover");

        var buckets = await BucketsAsync(ladder.Rollup, ladder.SeriesX);
        buckets[lastLegacyBucket].Should().BeApproximately(LegacyValue + CorrectionTotal, Tolerance);
        buckets[firstNativeBucket].Should().BeApproximately(NativeValue + CorrectionTotal, Tolerance);
    }

    [Fact]
    public async Task TC_REC_08_ADependentAboveTheMultiSourceRung_IsRecomputedTransitively()
    {
        fixture.OutputHelper = output;
        var ladder = await BuildLadderAsync("RecChain");

        // A 2 h rung on top of the multi-source rung. It knows nothing about the cutover.
        var top = await _builder.CreateRollupAsync("RecChainTop",
            [new RollupSourceReference(ladder.Rollup)], TimeSpan.FromHours(2),
            MultiSourceArchiveBuilder.CascadeSum);
        await _builder.ActivateAsync(top);
        await _builder.RewindAsync(top, ladder.Anchor);
        await _builder.TickAsync(top);

        // The 2 h rung is fixed-size: its buckets sit on even hours regardless of where the ladder's
        // anchor falls, so the bucket that contains the corrected hour is that hour aligned down to
        // the 2 h grid. Both hours of that pair are served by the native source.
        var correctedBucket = ladder.Cutover.AddHours(1);
        var topBucket = correctedBucket.Hour % 2 == 0 ? correctedBucket : correctedBucket.AddHours(-1);
        (await BucketsAsync(top, ladder.SeriesX))[topBucket]
            .Should().BeApproximately(NativeValue * 2, Tolerance);

        await WriteLateValuesAsync(ladder.Native, ladder.SeriesX, correctedBucket);

        // Propagate, then recompute the multi-source rung, then drain its dependent. The two steps
        // are deliberately NOT run inside one drain pass: the recompute's post-flip sweep of the
        // superseded generation reaches CrateDB's read path asynchronously, and the aggregation a
        // dependent runs over a rollup source carries no generation filter — a dependent recomputed
        // in the same pass sums the old AND the new generation of the corrected bucket. The chain
        // propagation itself is the production one: recomputing the rung enqueues the stale range on
        // its dependent, and the drain below consumes it.
        await (await OrchestratorAsync()).PropagateDirtyWindowsAsync(ladder.Native, CancellationToken.None);
        var pending = await PendingRangesAsync(ladder.Rollup);
        pending.Should().ContainSingle("the correction made exactly one bucket of the rung stale");

        await _builder.RecomputeAsync(ladder.Rollup, pending[0].RangeStart, pending[0].RangeEnd);
        await (await _builder.TenantAsync()).GetArchiveRecomputeStateStore()
            .ClearPendingRecomputeRangesAsync(ladder.Rollup);

        (await PendingRangesAsync(top)).Should().ContainSingle(
            "the completed recompute marked the dependent above the multi-source rung stale");

        await DrainAsync();

        (await BucketsAsync(ladder.Rollup, ladder.SeriesX))[correctedBucket]
            .Should().BeApproximately(NativeValue + CorrectionTotal, Tolerance);
        (await BucketsAsync(top, ladder.SeriesX))[topBucket]
            .Should().BeApproximately(NativeValue * 2 + CorrectionTotal, Tolerance,
                "the correction propagated up through the multi-source rung to its dependent");
    }

    [Fact]
    public async Task AnRtIdScopedManualRecompute_TouchesOnlyThatSeries()
    {
        fixture.OutputHelper = output;
        var ladder = await BuildLadderAsync("RecScoped");
        var correctedBucket = ladder.Anchor.AddHours(1);

        await WriteLateValuesAsync(ladder.Legacy, ladder.SeriesX, correctedBucket);
        await WriteLateValuesAsync(ladder.Legacy, ladder.SeriesY, correctedBucket);

        var job = await _builder.RecomputeAsync(
            ladder.Rollup, correctedBucket, correctedBucket.AddHours(1), ladder.SeriesX);
        job.State.Should().Be(RecomputeJobState.Completed);
        job.RtIdScope.Should().Be(ladder.SeriesX);

        (await BucketsAsync(ladder.Rollup, ladder.SeriesX))[correctedBucket]
            .Should().BeApproximately(LegacyValue + CorrectionTotal, Tolerance);
        (await BucketsAsync(ladder.Rollup, ladder.SeriesY))[correctedBucket]
            .Should().BeApproximately(LegacyValue, Tolerance,
                "a scoped recompute leaves every other series at its previous value");
    }

    [Fact]
    public async Task ASingleTimestampCorrection_SchedulesExactlyOneBucketRecomputeOnTheDependent()
    {
        // The retroactive-write detector builds the dirty window as [earliest, latest + 1 tick) so a
        // correction consisting of ONE timestamp still covers a non-empty interval. Mongo stores
        // DateTime at millisecond resolution, so that single tick is lost and the persisted window
        // is degenerate ([t, t)). Since AB#5157 the propagation clips the window to the writing
        // source's validity span BEFORE aligning it to buckets, and RollupSourceReference.Clip
        // requires from < to — which would discard the correction outright. The orchestrator
        // therefore widens a degenerate window back to one tick before clipping it, so a
        // single-point correction (the most common shape of a corrected meter reading) still
        // enqueues exactly the one bucket that contains it.
        fixture.OutputHelper = output;
        var ladder = await BuildLadderAsync("RecSingleTimestamp");
        var correctedBucket = ladder.Anchor.AddHours(1);
        var before = await BucketsAsync(ladder.Rollup, ladder.SeriesX);
        before[correctedBucket].Should().BeApproximately(LegacyValue, Tolerance,
            "the bucket carries the seeded legacy value before the correction");

        await _builder.InsertPointsAsync(ladder.Legacy, ladder.SeriesX,
            [(correctedBucket.AddMinutes(30), CorrectionValue)]);

        var windows = await DirtyWindowsAsync(ladder.Legacy);
        windows.Should().ContainSingle("the write IS detected as retroactive");
        windows[0].WindowEnd.Should().Be(windows[0].WindowStart,
            "the one-tick width the detector added does not survive Mongo's millisecond resolution");

        await (await OrchestratorAsync()).PropagateDirtyWindowsAsync(ladder.Legacy, CancellationToken.None);

        var range = (await PendingRangesAsync(ladder.Rollup)).Should().ContainSingle(
            "the degenerate window is widened to one tick before it is clipped to the legacy span, "
            + "so it still aligns to exactly one bucket").Subject;
        range.RangeStart.Should().Be(correctedBucket);
        range.RangeEnd.Should().Be(correctedBucket.AddHours(1),
            "exactly the bucket that contains the corrected timestamp — no neighbour is dragged in");

        await DrainAsync();

        (await CompletedJobsAsync(ladder.Rollup)).Should().ContainSingle(
            "the pending range was recomputed once");
        (await BucketsAsync(ladder.Rollup, ladder.SeriesX))[correctedBucket]
            .Should().BeApproximately(LegacyValue + CorrectionValue, Tolerance,
                "the single-point correction reached the dependent");
    }

    // ── ladder + helpers ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A populated two-source hourly ladder ending at the current open bucket, so a write into the
    /// recent past is genuinely retroactive.
    /// </summary>
    private sealed record RecomputeLadder(
        OctoObjectId Legacy,
        OctoObjectId Native,
        OctoObjectId Rollup,
        OctoObjectId SeriesX,
        OctoObjectId SeriesY,
        OctoObjectId SeriesZ,
        DateTime Anchor,
        DateTime Cutover);

    private async Task<RecomputeLadder> BuildLadderAsync(string namePrefix)
    {
        var anchor = AlignDownToHour(DateTime.UtcNow).AddHours(-6);
        var cutover = anchor.AddHours(3);

        var legacy = await _builder.CreateRawArchiveAsync($"{namePrefix}Legacy");
        var native = await _builder.CreateRawArchiveAsync($"{namePrefix}Native");
        var seriesX = OctoObjectId.GenerateNewId();
        var seriesY = OctoObjectId.GenerateNewId();

        foreach (var series in new[] { seriesX, seriesY })
        {
            await _builder.InsertPointsAsync(legacy, series,
                Enumerable.Range(0, 3).Select(i => (anchor.AddHours(i), LegacyValue)));
            await _builder.InsertPointsAsync(native, series,
                Enumerable.Range(0, 3).Select(i => (cutover.AddHours(i), NativeValue)));
        }

        var rollupRtId = await _builder.CreateRollupAsync($"{namePrefix}Rollup",
            [
                new RollupSourceReference(legacy, ValidTo: cutover),
                new RollupSourceReference(native, ValidFrom: cutover),
            ],
            OneHour);
        await _builder.ActivateAsync(rollupRtId);
        await _builder.RewindAsync(rollupRtId, anchor);

        // The anchor is pinned to the hour grid, so the number of closed buckets is a function of the
        // wall clock, not a constant: an hour boundary passing while the ladder is built adds one.
        // Bracket the tick with the clock instead of hard-coding six.
        var closedBefore = (int)(AlignDownToHour(DateTime.UtcNow) - anchor).TotalHours;
        var ticked = await _builder.TickAsync(rollupRtId);
        var closedAfter = (int)(AlignDownToHour(DateTime.UtcNow) - anchor).TotalHours;
        ticked.Should().BeInRange(closedBefore, closedAfter,
            "the ladder covers exactly the closed buckets between the anchor and the currently open one");

        return new RecomputeLadder(legacy, native, rollupRtId, seriesX, seriesY,
            OctoObjectId.GenerateNewId(), anchor, cutover);
    }

    /// <summary>
    /// Writes a correction into <paramref name="bucketStart"/>: two late points at distinct
    /// timestamps, so the dirty window the detector records still covers a non-empty interval after
    /// Mongo's millisecond truncation — the degenerate single-timestamp shape is covered separately
    /// by <see cref="ASingleTimestampCorrection_SchedulesExactlyOneBucketRecomputeOnTheDependent"/>.
    /// </summary>
    private Task WriteLateValuesAsync(
        OctoObjectId archiveRtId, OctoObjectId seriesRtId, DateTime bucketStart, int offsetMinutes = 30) =>
        _builder.InsertPointsAsync(archiveRtId, seriesRtId,
        [
            (bucketStart.AddMinutes(offsetMinutes), CorrectionValue),
            (bucketStart.AddMinutes(offsetMinutes + 5), CorrectionValue),
        ]);

    /// <summary>
    /// One pass of the production drain: fan every source's dirty windows out onto its dependents,
    /// then recompute each rollup's coalesced pending ranges.
    /// </summary>
    private async Task DrainAsync() =>
        await (await OrchestratorAsync()).TickAsync(CancellationToken.None);

    private async Task<IRecomputeOrchestrator> OrchestratorAsync() =>
        (await _builder.TenantAsync()).GetRecomputeOrchestrator()!;

    private async Task<IReadOnlyList<ArchiveDirtyWindow>> DirtyWindowsAsync(OctoObjectId archiveRtId) =>
        await (await _builder.TenantAsync()).GetArchiveRecomputeStateStore().GetDirtyWindowsAsync(archiveRtId);

    private async Task<IReadOnlyList<ArchiveRecomputeRange>> PendingRangesAsync(OctoObjectId rollupRtId) =>
        await (await _builder.TenantAsync()).GetArchiveRecomputeStateStore()
            .GetPendingRecomputeRangesAsync(rollupRtId);

    /// <summary>Completed recompute jobs of one rollup, oldest first.</summary>
    private async Task<IReadOnlyList<RecomputeJobSnapshot>> CompletedJobsAsync(OctoObjectId rollupRtId)
    {
        var jobs = await (await _builder.TenantAsync()).GetRecomputeJobStore().GetForArchiveAsync(rollupRtId, 50);
        return jobs.Where(j => j.State == RecomputeJobState.Completed)
            .OrderBy(j => j.StartedAt)
            .ToList();
    }

    /// <summary>The rollup's materialised buckets for one series as <c>window start → value</c>.</summary>
    private async Task<IReadOnlyDictionary<DateTime, double>> BucketsAsync(
        OctoObjectId rollupRtId, OctoObjectId seriesRtId)
    {
        var buckets = await _builder.ReadBucketsAsync(
            rollupRtId, MultiSourceArchiveBuilder.RollupColumn, seriesRtId);
        return buckets.Where(b => b.Value is not null)
            .ToDictionary(b => b.WindowStart, b => b.Value!.Value);
    }

    private static DateTime AlignDownToHour(DateTime value) =>
        new(value.Year, value.Month, value.Day, value.Hour, 0, 0, DateTimeKind.Utc);
}
