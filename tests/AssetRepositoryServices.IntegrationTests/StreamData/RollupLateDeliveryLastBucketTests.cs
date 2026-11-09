using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
/// AB#8621 — a delivery that arrives after the hourly rollup closed its buckets (by the clock, without data) and is
/// followed by a pause fills every bucket it covers, including the last one, end to end against a real MongoDB and
/// CrateDB: the ingest detector records the dirty window, MongoDB stores it, the production drain recomputes it.
/// </summary>
/// <remarks>
/// Hourly time-range rows are keyed by their window start, which is always on the hour, i.e. on a bucket boundary of
/// the hourly rollup. Before the fix the stored dirty window ended exactly at the latest window start (the one-tick
/// end did not survive MongoDB's millisecond dates), so the recompute stopped one bucket short and nothing after the
/// pause ever covered that bucket again (voestalpine, hourly rollup <c>6a4268000000000000000001</c>: 13.10. 03:00Z on
/// test-2, 30.10. 04:00Z on staging-1).
/// </remarks>
[Collection(StreamDataImmediateSweepCollection.Name)]
public class RollupLateDeliveryLastBucketTests(ImmediateSweepStreamDataFixture fixture, ITestOutputHelper output)
{
    private readonly MultiSourceArchiveBuilder _builder = new(fixture);

    private static readonly TimeSpan OneHour = TimeSpan.FromHours(1);
    private const double Delivered = 5d;
    private const double Tolerance = 1e-9;

    [Fact]
    public async Task ALateDeliveryBeforeAPause_FillsItsLastHourlyBucket()
    {
        fixture.OutputHelper = output;
        // The delivered hours end three hours before now: the forward orchestrator has closed every bucket of them and
        // the buckets after them, and nothing follows the delivery (the pause).
        var anchor = AlignDownToHour(DateTime.UtcNow).AddHours(-7);
        var series = OctoObjectId.GenerateNewId();
        var archive = await _builder.CreateTimeRangeArchiveAsync("LateDeliverySource", OneHour);
        var rollup = await _builder.CreateRollupAsync("LateDeliveryHourly", [new RollupSourceReference(archive)], OneHour);
        await _builder.ActivateAsync(rollup);
        await _builder.RewindAsync(rollup, anchor);
        (await _builder.TickAsync(rollup)).Should().BeGreaterThanOrEqualTo(6, "the clock closes the buckets without data");
        (await RowStartsAsync(rollup, series)).Should().BeEmpty("no data had arrived when the buckets closed, so no row was written");

        var hours = Enumerable.Range(0, 4).Select(i => anchor + i * OneHour).ToList();
        await _builder.InsertWindowsAsync(archive, series, hours.Select(h => (h, h + OneHour, Delivered)));

        var window = (await (await _builder.TenantAsync()).GetArchiveRecomputeStateStore().GetDirtyWindowsAsync(archive))
            .Should().ContainSingle("the late delivery is retroactive").Subject;
        window.WindowEnd.Should().BeAfter(hours[^1], "the stored window still holds the latest delivered hour");

        await (await _builder.TenantAsync()).GetRecomputeOrchestrator()!.TickAsync(CancellationToken.None);

        // The rows as stored, null aggregates included, so a row written after the pause cannot hide behind a null value.
        (await RowStartsAsync(rollup, series)).Should().Equal(hours,
            "exactly the delivered hours have a row, the last one before the pause included, and nothing after the pause");

        var buckets = await BucketsAsync(rollup, series);
        foreach (var hour in hours)
        {
            buckets.Should().ContainKey(hour, "every delivered hour gets its bucket, the last one before the pause included");
            buckets[hour].Should().BeApproximately(Delivered, Tolerance);
        }


        var job = (await (await _builder.TenantAsync()).GetRecomputeJobStore().GetForArchiveAsync(rollup, 50))
            .Where(j => j.State == RecomputeJobState.Completed).Should().ContainSingle().Subject;
        job.RangeStart.Should().Be(hours[0]);
        job.RangeEnd.Should().Be(hours[^1] + OneHour, "the range ends after the bucket of the latest delivered hour");
    }

    private async Task<IReadOnlyList<DateTime>> RowStartsAsync(OctoObjectId rollupRtId, OctoObjectId seriesRtId) =>
        (await _builder.ReadBucketsAsync(rollupRtId, MultiSourceArchiveBuilder.RollupColumn, seriesRtId))
        .Select(b => b.WindowStart).Order().ToList();

    private async Task<IReadOnlyDictionary<DateTime, double>> BucketsAsync(OctoObjectId rollupRtId, OctoObjectId seriesRtId)
    {
        var buckets = await _builder.ReadBucketsAsync(rollupRtId, MultiSourceArchiveBuilder.RollupColumn, seriesRtId);
        return buckets.Where(b => b.Value is not null).ToDictionary(b => b.WindowStart, b => b.Value!.Value);
    }

    private static DateTime AlignDownToHour(DateTime value) =>
        new(value.Year, value.Month, value.Day, value.Hour, 0, 0, DateTimeKind.Utc);
}
