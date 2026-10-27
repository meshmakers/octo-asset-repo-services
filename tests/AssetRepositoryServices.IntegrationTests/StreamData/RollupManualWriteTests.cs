using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
/// AB#7594 — a manual write that replaces a delivered value in a time-range archive reaches the rollup
/// buckets over that window, end to end against a real CrateDB: the ingest detector records a manual
/// dirty window, the production drain recomputes exactly the bucket that contains it, the bucket then
/// holds the manual value in place of the replaced one (no double counting) and the rollup validator
/// finds nothing.
/// </summary>
/// <remarks>
/// The archive is anchored a few hours before <c>UtcNow</c> for the same reason as in
/// <see cref="RollupMultiSourceRecomputeTests" />: the forward orchestrator commits closed buckets only,
/// so only a window in the recent past is behind the consumed watermark.
/// </remarks>
[Collection(StreamDataImmediateSweepCollection.Name)]
public class RollupManualWriteTests(ImmediateSweepStreamDataFixture fixture, ITestOutputHelper output)
{
    private readonly MultiSourceArchiveBuilder _builder = new(fixture);

    private static readonly TimeSpan OneHour = TimeSpan.FromHours(1);
    private static readonly TimeSpan QuarterHour = TimeSpan.FromMinutes(15);
    private const double Delivered = 1d;
    private const double Corrected = 10d;
    private const double Tolerance = 1e-9;

    [Fact]
    public async Task AManualValueReplacingADeliveredOne_ShowsInTheRollupBucket_WithoutDoubleCounting()
    {
        fixture.OutputHelper = output;
        var anchor = AlignDownToHour(DateTime.UtcNow).AddHours(-4);
        var series = OctoObjectId.GenerateNewId();
        var archive = await _builder.CreateTimeRangeArchiveAsync("ManualWriteSource", QuarterHour);
        await _builder.InsertWindowsAsync(archive, series,
            Enumerable.Range(0, 12).Select(i => (anchor + i * QuarterHour, anchor + (i + 1) * QuarterHour, Delivered)));

        var rollup = await _builder.CreateRollupAsync("ManualWriteRollup", [new RollupSourceReference(archive)], OneHour);
        await _builder.ActivateAsync(rollup);
        await _builder.RewindAsync(rollup, anchor);
        await _builder.TickAsync(rollup);

        var correctedBucket = anchor.AddHours(1);
        (await BucketsAsync(rollup, series))[correctedBucket].Should().BeApproximately(4 * Delivered, Tolerance,
            "four delivered quarter hours per bucket before the correction");

        // The manual correction of one quarter hour inside the second bucket.
        var repo = (await _builder.TenantAsync()).GetStreamDataRepository()!;
        await repo.InsertTimeRangeAsync(archive,
        [
            new TimeRangeStreamDataPoint
            {
                RtId = series,
                CkTypeId = new RtCkId<CkTypeId>(fixture.TestCkTypeId),
                From = correctedBucket.AddMinutes(15),
                To = correctedBucket.AddMinutes(30),
                Attributes = new Dictionary<string, object?> { [MultiSourceArchiveBuilder.BaseColumn] = Corrected },
                Origin = ArchiveRowOrigin.Manual,
                ManualWrittenBy = "user-1",
                ManualReason = "meter exchanged"
            }
        ], TestContext.Current.CancellationToken);
        await _builder.RefreshAsync(archive);

        var windows = await (await _builder.TenantAsync()).GetArchiveRecomputeStateStore().GetDirtyWindowsAsync(archive);
        windows.Should().ContainSingle(w => w.Source == RecomputeChangeSource.Manual,
            "the manual write records a dirty window of its own");

        await (await _builder.TenantAsync()).GetRecomputeOrchestrator()!.TickAsync(CancellationToken.None);

        var buckets = await BucketsAsync(rollup, series);
        buckets[correctedBucket].Should().BeApproximately(3 * Delivered + Corrected, Tolerance,
            "the manual value replaces the delivered one in the bucket; the replaced value is not counted twice");
        buckets[anchor].Should().BeApproximately(4 * Delivered, Tolerance, "the bucket before is untouched");
        buckets[anchor.AddHours(2)].Should().BeApproximately(4 * Delivered, Tolerance, "the bucket after is untouched");

        var jobs = (await (await _builder.TenantAsync()).GetRecomputeJobStore().GetForArchiveAsync(rollup, 50))
            .Where(j => j.State == RecomputeJobState.Completed).ToList();
        var job = jobs.Should().ContainSingle("one manual write, one recompute").Subject;
        job.RangeStart.Should().Be(correctedBucket);
        job.RangeEnd.Should().Be(correctedBucket.AddHours(1), "only the affected bucket is recomputed");

        var report = await repo.ValidateRollupsAsync([rollup], TestContext.Current.CancellationToken);
        report.Rollups.Should().ContainSingle(r => r.RollupRtId == rollup).Which.Findings.Should().BeEmpty(
            "the rollup is consistent with its source after the manual write");
    }

    private async Task<IReadOnlyDictionary<DateTime, double>> BucketsAsync(OctoObjectId rollupRtId, OctoObjectId seriesRtId)
    {
        var buckets = await _builder.ReadBucketsAsync(rollupRtId, MultiSourceArchiveBuilder.RollupColumn, seriesRtId);
        return buckets.Where(b => b.Value is not null).ToDictionary(b => b.WindowStart, b => b.Value!.Value);
    }

    private static DateTime AlignDownToHour(DateTime value) =>
        new(value.Year, value.Month, value.Day, value.Hour, 0, 0, DateTimeKind.Utc);
}
