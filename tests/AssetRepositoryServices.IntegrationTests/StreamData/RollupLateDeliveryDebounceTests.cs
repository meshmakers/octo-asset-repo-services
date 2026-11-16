using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
/// AB#9016 / AB#9099 — with the production late-data debounce a late automated delivery is held back, end to end against a
/// real MongoDB and CrateDB: the first drain after the delivery runs no recompute and leaves the rollup as it was, and the
/// obligation it stores carries the hold (<see cref="ArchiveRecomputeRange.NextAttemptAt"/> one debounce interval after it was
/// enqueued, at attempt zero) and exactly the series of the delivery. That the held obligation later runs once, and the late
/// deliveries of one interval together, is tested with a fake clock in octo-construction-kit-engine
/// (<c>RecomputeLateDeliveryTests</c>); where the values end up is tested on the zero-debounce fixture
/// (<see cref="RollupMultiSourceRecomputeTests"/>, <see cref="RollupLateDeliveryLastBucketTests"/>).
/// </summary>
[Collection(StreamDataDefaultDebounceCollection.Name)]
public class RollupLateDeliveryDebounceTests(DefaultDebounceStreamDataFixture fixture, ITestOutputHelper output)
{
    private readonly MultiSourceArchiveBuilder _builder = new(fixture);

    private static readonly TimeSpan OneHour = TimeSpan.FromHours(1);
    private const double Delivered = 5d;

    [Fact]
    public async Task ALateDelivery_IsHeldForTheDebounce_WithItsSeries_AndTheFirstDrainRunsNothing()
    {
        fixture.OutputHelper = output;
        var anchor = AlignDownToHour(DateTime.UtcNow).AddHours(-5);
        var series = OctoObjectId.GenerateNewId();
        var archive = await _builder.CreateTimeRangeArchiveAsync("DebounceSource", OneHour);
        var rollup = await _builder.CreateRollupAsync("DebounceHourly", [new RollupSourceReference(archive)], OneHour);
        await _builder.ActivateAsync(rollup);
        await _builder.RewindAsync(rollup, anchor);
        (await _builder.TickAsync(rollup)).Should().BeGreaterThanOrEqualTo(4, "the clock closes the buckets without data");

        var hours = Enumerable.Range(0, 2).Select(i => anchor + i * OneHour).ToList();
        await _builder.InsertWindowsAsync(archive, series, hours.Select(h => (h, h + OneHour, Delivered)));

        var tenant = await _builder.TenantAsync();
        (await tenant.GetArchiveRecomputeStateStore().GetDirtyWindowsAsync(archive))
            .Should().ContainSingle("the late delivery is retroactive");

        await tenant.GetRecomputeOrchestrator()!.TickAsync(CancellationToken.None);

        (await tenant.GetRecomputeJobStore().GetForArchiveAsync(rollup, 50))
            .Where(j => j.State == RecomputeJobState.Completed)
            .Should().BeEmpty("the first drain holds the late delivery back for the debounce interval");
        (await _builder.ReadBucketsAsync(rollup, MultiSourceArchiveBuilder.RollupColumn, series))
            .Should().BeEmpty("nothing was recomputed yet");

        var held = (await tenant.GetArchiveRecomputeStateStore().GetPendingRecomputeRangesAsync(rollup))
            .Should().ContainSingle("the late delivery is one obligation on the rollup").Subject;
        held.RangeStart.Should().Be(hours[0]);
        held.RangeEnd.Should().Be(hours[^1] + OneHour);
        held.IsDebounced.Should().BeTrue("the hold is a debounce at attempt zero, not a retry backoff");
        held.NextAttemptAt.Should().BeCloseTo(held.EnqueuedAt + RecomputeLateDataOptions.DefaultLateDataDebounce,
            TimeSpan.FromSeconds(1), "the obligation is due one default debounce interval after it was enqueued");
        held.RtIdSet.Should().NotBeNull("the recompute is restricted to the series of the delivery").And.Equal([series]);
    }

    private static DateTime AlignDownToHour(DateTime value) =>
        new(value.Year, value.Month, value.Day, value.Hour, 0, 0, DateTimeKind.Utc);
}
