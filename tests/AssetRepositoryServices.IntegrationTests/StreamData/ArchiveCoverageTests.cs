using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.StreamData;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
/// AB#5157 — MEASURED archive coverage against a real CrateDB: what
/// <c>GetFamilyCoverageAsync</c> (behind <c>coverageFor</c> and <c>GET archives/{rtId}/coverage</c>)
/// reports per rung, and how the coverage memo behaves around a write.
/// <para>
/// Coverage is never derived: a rung reports the range its OWN table holds — MIN/MAX of the
/// timestamp for a raw archive, MIN(window_start)/MAX(window_end) for a windowed one (time-range
/// archive or rollup) — so an unpopulated rollup reports nothing even while its sources hold years
/// of history. The pair is a single interval: gaps inside it are invisible and there is no per-rtId
/// breakdown.
/// </para>
/// <para>
/// The ladders are built with <see cref="MultiSourceArchiveBuilder"/> and every test provisions its
/// OWN GUID-suffixed archives — including the two single-archive facts, which could have read the
/// fixture's seeded archives instead: those are shared with the rest of the collection and
/// <c>ComputedColumnLogicalNameTests</c> appends a row an hour past
/// <see cref="StreamDataFixture.TestDataEndTime"/> to the canonical one, which would make an exact
/// MIN/MAX assertion depend on test order.
/// </para>
/// </summary>
[Collection(StreamDataMutatingCollection.Name)]
public class ArchiveCoverageTests(StreamDataFixture fixture, ITestOutputHelper output)
{
    private readonly MultiSourceArchiveBuilder _builder = new(fixture);

    private static readonly TimeSpan OneHour = TimeSpan.FromHours(1);

    /// <summary>Bucket-aligned anchor of every synthetic history in this class.</summary>
    private static readonly DateTime T0 = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    // ── Measured coverage of a single archive ────────────────────────────────────────────────

    [Fact]
    public async Task TC_COV_01_RawArchiveCoverage_IsTheEarliestAndLatestStoredTimestamp()
    {
        fixture.OutputHelper = output;
        var archive = await _builder.CreateRawArchiveAsync("CovRaw");
        var series = OctoObjectId.GenerateNewId();
        await _builder.InsertPointsAsync(archive, series,
            [(T0, 1d), (T0.AddMinutes(30), 2d), (T0.AddHours(2), 3d)]);

        var rungs = await FamilyAsync(archive);

        var raw = rungs[0];
        raw.ArchiveRtId.Should().Be(archive, "the queried archive is reported first");
        raw.IsBase.Should().BeTrue();
        raw.Status.Should().Be(CkArchiveStatus.Activated);
        raw.BucketSizeMs.Should().BeNull("a raw archive declares no grain");
        raw.Alignment.Should().Be(BucketAlignment.FixedSize);
        raw.StoredFunctions.Should().BeEmpty("a base archive stores no aggregation");
        raw.AvailableFrom.Should().Be(T0, "coverage is MIN(timestamp) over the rows");
        raw.AvailableTo.Should().Be(T0.AddHours(2), "coverage is MAX(timestamp) over the rows");
    }

    [Fact]
    public async Task TimeRangeArchiveCoverage_SpansTheFirstWindowStartToTheLastWindowEnd()
    {
        fixture.OutputHelper = output;
        var period = TimeSpan.FromMinutes(15);
        var archive = await _builder.CreateTimeRangeArchiveAsync("CovWindowed", period);
        var series = OctoObjectId.GenerateNewId();
        await _builder.InsertWindowsAsync(archive, series,
            Enumerable.Range(0, 3).Select(i =>
                (T0.Add(period * i), T0.Add(period * (i + 1)), 100d + i)));

        var rungs = await FamilyAsync(archive);

        var windowed = rungs[0];
        windowed.IsBase.Should().BeTrue("a time-range archive is a base archive, not a rollup");
        windowed.BucketSizeMs.Should().Be((long)period.TotalMilliseconds,
            "a time-range archive reports its declared Period as its grain");
        windowed.Alignment.Should().Be(BucketAlignment.FixedSize);
        windowed.AvailableFrom.Should().Be(T0, "windowed coverage starts at MIN(window_start)");
        windowed.AvailableTo.Should().Be(T0.Add(period * 3),
            "windowed coverage ends at MAX(window_end) — the exclusive end of the last window, "
            + "not the start of it");
    }

    [Fact]
    public async Task TC_COV_03_CoverageSpansEveryRtIdOfTheArchive_WithNoPerSeriesBreakdown()
    {
        fixture.OutputHelper = output;
        var archive = await _builder.CreateRawArchiveAsync("CovMultiSeries");

        var early = OctoObjectId.GenerateNewId();
        var late = OctoObjectId.GenerateNewId();
        await _builder.InsertPointsAsync(archive, early,
            [(T0, 1d), (T0.AddHours(3), 2d)]);
        await _builder.InsertPointsAsync(archive, late,
            [(T0.AddHours(10), 3d), (T0.AddHours(12), 4d)]);

        var rungs = await FamilyAsync(archive);

        rungs.Should().ContainSingle("the archive has no rollups yet");
        rungs[0].AvailableFrom.Should().Be(T0, "the earliest row of ANY series opens the coverage");
        rungs[0].AvailableTo.Should().Be(T0.AddHours(12), "the latest row of ANY series closes it");
    }

    [Fact]
    public async Task TC_COV_04_AGapInsideTheArchive_IsInvisibleInCoverage()
    {
        fixture.OutputHelper = output;
        var archive = await _builder.CreateRawArchiveAsync("CovGap");
        var series = OctoObjectId.GenerateNewId();

        // Two blocks, twelve empty hours between them.
        await _builder.InsertPointsAsync(archive, series,
            [(T0, 1d), (T0.AddHours(1), 2d), (T0.AddHours(13), 3d), (T0.AddHours(14), 4d)]);

        var rungs = await FamilyAsync(archive);

        rungs.Should().ContainSingle("coverage is ONE interval per rung — a gap never splits it");
        rungs[0].AvailableFrom.Should().Be(T0);
        rungs[0].AvailableTo.Should().Be(T0.AddHours(14),
            "the pair spans the gap; the empty hours are not represented anywhere in the result");
    }

    [Fact]
    public async Task TC_COV_05_AnActivatedButEmptyArchive_ReportsNoCoverage()
    {
        fixture.OutputHelper = output;
        var archive = await _builder.CreateRawArchiveAsync("CovEmpty");

        var rungs = await FamilyAsync(archive);

        rungs.Should().ContainSingle("the query succeeds — an empty table is not an error");
        rungs[0].Status.Should().Be(CkArchiveStatus.Activated);
        rungs[0].AvailableFrom.Should().BeNull("an empty table has no coverage, not an epoch sentinel");
        rungs[0].AvailableTo.Should().BeNull();
    }

    [Fact]
    public async Task TC_COV_02_And_16_RollupCoverage_IsItsOwnStoredRange_BeforeAndAfterBackfill()
    {
        fixture.OutputHelper = output;
        var source = await _builder.CreateRawArchiveAsync("CovRollupSource");
        var series = OctoObjectId.GenerateNewId();
        await _builder.InsertPointsAsync(source, series,
            Enumerable.Range(0, 7).Select(i => (T0.AddHours(i), 1d)));

        var rollup = await _builder.CreateRollupAsync(
            "CovRollup", [new RollupSourceReference(source)], OneHour);
        await _builder.ActivateAsync(rollup);

        // TC-COV-16: activated, never aggregated — the sources' seven hours are NOT inferred.
        var beforeBackfill = (await FamilyAsync(source))[1];
        beforeBackfill.ArchiveRtId.Should().Be(rollup);
        beforeBackfill.IsBase.Should().BeFalse();
        beforeBackfill.AvailableFrom.Should().BeNull(
            "coverage is measured on the rung's own table, never derived from what its sources could yield");
        beforeBackfill.AvailableTo.Should().BeNull();

        // TC-COV-07: backfill exactly two of the seven hours. A completed recompute drops the
        // memoised answer (RecomputeOrchestrator invalidates it), so the next read re-measures
        // without waiting out the TTL.
        await _builder.RecomputeAsync(rollup, T0.AddHours(2), T0.AddHours(4));

        var afterBackfill = (await FamilyAsync(source))[1];
        afterBackfill.AvailableFrom.Should().Be(T0.AddHours(2),
            "coverage moved back to the earliest backfilled bucket");
        afterBackfill.AvailableTo.Should().Be(T0.AddHours(4),
            "and forward to the exclusive end of the last backfilled bucket");
        afterBackfill.AvailableFrom.Should().BeAfter(T0,
            "the rung reports what it stored — a strict subset of its source's range");
        afterBackfill.AvailableTo.Should().BeBefore(T0.AddHours(6));
    }

    // ── The coverage memo ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TC_COV_08_CoverageIsServedFromTheMemo_WithinTheFreshnessWindow()
    {
        fixture.OutputHelper = output;
        var archive = await _builder.CreateRawArchiveAsync("CovMemo");
        var series = OctoObjectId.GenerateNewId();
        await _builder.InsertPointsAsync(archive, series, [(T0, 1d), (T0.AddHours(2), 2d)]);

        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = (await _builder.TenantAsync()).GetStreamDataRepository()!;
        // A memo whose TTL outlives the test, so "within the freshness window" is not a race with
        // the wall clock — the host's own TTL is asserted in TC-COV-09.
        var memo = new ArchiveCoverageCache(TimeSpan.FromMinutes(5));
        var provider = new CachedArchiveCoverageProvider(fixture.StreamDataTenantId, repository, memo);

        var warmed = await provider.GetCoverageAsync(archive, cancellationToken);
        warmed!.AvailableTo.Should().Be(T0.AddHours(2));

        await _builder.InsertPointsAsync(archive, series, [(T0.AddHours(5), 3d)]);

        (await provider.GetCoverageAsync(archive, cancellationToken)).Should().Be(warmed,
            "inside the freshness window the memoised pair is returned unchanged — ordinary ingest "
            + "is absorbed by the TTL");
        (await repository.GetArchiveCoverageAsync(archive, cancellationToken))!.AvailableTo
            .Should().Be(T0.AddHours(5),
                "the storage layer already sees the new row, so the previous answer really came from the memo");

        memo.Invalidate(fixture.StreamDataTenantId, archive);
        (await provider.GetCoverageAsync(archive, cancellationToken))!.AvailableTo.Should().Be(T0.AddHours(5),
            "an invalidated entry is re-measured");
    }

    [Fact]
    public async Task TC_COV_09_CoverageIsRemeasured_AfterTheConfiguredTtlElapses()
    {
        fixture.OutputHelper = output;
        var ttl = fixture.GetService<ArchiveCoverageCache>().CacheTtl;
        ttl.Should().Be(TimeSpan.FromSeconds(1),
            "the test host binds StreamData:Coverage:CacheTtlSeconds the same way Program.cs does");

        var archive = await _builder.CreateRawArchiveAsync("CovTtl");
        var series = OctoObjectId.GenerateNewId();
        await _builder.InsertPointsAsync(archive, series, [(T0, 1d), (T0.AddHours(2), 2d)]);

        var cancellationToken = TestContext.Current.CancellationToken;
        var provider = (await _builder.TenantAsync()).GetArchiveCoverageProvider()!;
        var before = await provider.GetCoverageAsync(archive, cancellationToken);
        before!.AvailableTo.Should().Be(T0.AddHours(2));

        await _builder.InsertPointsAsync(archive, series, [(T0.AddHours(9), 3d)]);
        await Task.Delay(ttl + TimeSpan.FromMilliseconds(300), cancellationToken);

        var after = await provider.GetCoverageAsync(archive, cancellationToken);
        after!.AvailableTo.Should().Be(T0.AddHours(9),
            "once the TTL has elapsed the next request measures again and sees the new row");
        after.AvailableFrom.Should().Be(T0, "the start of the range is unaffected by a later write");
    }

    // ── Family enumeration ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ADisabledRung_IsListedWithItsStatus_AndKeepsItsMeasuredCoverage()
    {
        fixture.OutputHelper = output;
        var source = await _builder.CreateRawArchiveAsync("CovDisabledSource");
        var series = OctoObjectId.GenerateNewId();
        await _builder.InsertPointsAsync(source, series,
            [(T0, 1d), (T0.AddHours(1), 2d)]);

        var rollup = await _builder.CreateRollupAsync(
            "CovDisabledRollup", [new RollupSourceReference(source)], OneHour);
        await _builder.ActivateAsync(rollup);
        await _builder.RecomputeAsync(rollup, T0, T0.AddHours(2));

        var lifecycle = (await _builder.TenantAsync()).GetArchiveLifecycleService()!;
        await lifecycle.DisableAsync(rollup);

        var disabled = (await FamilyAsync(source))[1];
        disabled.ArchiveRtId.Should().Be(rollup);
        disabled.Status.Should().Be(CkArchiveStatus.Disabled,
            "the status is what lets a client tell a disabled rung from an empty one");
        disabled.AvailableFrom.Should().Be(T0,
            "disabling preserves the CrateDB table, so the measured coverage stands");
        disabled.AvailableTo.Should().Be(T0.AddHours(2));
    }

    [Fact]
    public async Task ARungReachableOverTwoPaths_IsListedExactlyOnce()
    {
        fixture.OutputHelper = output;
        var source = await _builder.CreateRawArchiveAsync("CovDiamondSource");

        var hourly = await _builder.CreateRollupAsync(
            "CovDiamondHourly", [new RollupSourceReference(source)], OneHour);
        var twoHourly = await _builder.CreateRollupAsync(
            "CovDiamondTwoHourly", [new RollupSourceReference(source)], TimeSpan.FromHours(2));

        // Both parents are reachable from the same base, so the four-hourly rung closes a diamond.
        var split = T0.AddHours(4);
        var fourHourly = await _builder.CreateRollupAsync("CovDiamondTop",
            [
                new RollupSourceReference(hourly, ValidTo: split),
                new RollupSourceReference(twoHourly, ValidFrom: split),
            ],
            TimeSpan.FromHours(4), MultiSourceArchiveBuilder.CascadeSum);

        var rungs = await FamilyAsync(source);

        var expected = new[] { source, hourly, twoHourly, fourHourly };
        rungs.Select(r => r.ArchiveRtId).Should().Equal(expected,
            "breadth-first from the queried archive: both direct dependents, then the rung above them");
        rungs.Select(r => r.ArchiveRtId).Should().OnlyHaveUniqueItems(
            "a rung reachable over several paths is still listed once");
    }

    [Fact]
    public async Task TC_COV_13_AMultiSourceRung_AppearsInBothFamilies_WithIdenticalCoverage()
    {
        fixture.OutputHelper = output;
        var legacy = await _builder.CreateRawArchiveAsync("CovFamilyLegacy");
        var native = await _builder.CreateRawArchiveAsync("CovFamilyNative");
        var series = OctoObjectId.GenerateNewId();
        var cutover = T0.AddHours(2);

        await _builder.InsertPointsAsync(legacy, series, [(T0, 10d), (T0.AddHours(1), 11d)]);
        await _builder.InsertPointsAsync(native, series, [(cutover, 20d), (cutover.AddHours(1), 21d)]);

        var rung = await _builder.CreateRollupAsync("CovFamilyRung",
            [
                new RollupSourceReference(legacy, ValidTo: cutover),
                new RollupSourceReference(native, ValidFrom: cutover),
            ],
            OneHour);
        await _builder.ActivateAsync(rung);
        await _builder.RecomputeAsync(rung, T0, cutover.AddHours(2));

        var fromLegacy = (await FamilyAsync(legacy)).Single(r => r.ArchiveRtId == rung);
        var fromNative = (await FamilyAsync(native)).Single(r => r.ArchiveRtId == rung);

        fromLegacy.AvailableFrom.Should().Be(T0, "the rung spans both sources' spans");
        fromLegacy.AvailableTo.Should().Be(cutover.AddHours(2));
        fromNative.Should().BeEquivalentTo(fromLegacy,
            "a multi-source rung belongs to every source's family and reports its OWN coverage in "
            + "each — never a per-family subset");
    }

    [Fact]
    public async Task RungFacts_CarryTheDeclaredGrain_Alignment_AndStoredFunctions()
    {
        fixture.OutputHelper = output;
        var legacy = await _builder.CreateTimeRangeArchiveAsync(
            "CovFactsLegacy", TimeSpan.FromMinutes(15));

        var daily = await _builder.CreateRollupAsync("CovFactsDaily",
            [new RollupSourceReference(legacy)], TimeSpan.FromDays(1),
            [
                new CkRollupAggregationSpec(MultiSourceArchiveBuilder.VoltagePath, CkRollupFunction.Sum, null),
                new CkRollupAggregationSpec(MultiSourceArchiveBuilder.VoltagePath, CkRollupFunction.Max, null),
            ],
            BucketAlignment.CalendarDay);

        var rungs = await FamilyAsync(legacy);
        rungs.Should().HaveCount(2);

        rungs[0].IsBase.Should().BeTrue("a time-range archive is a base archive");
        rungs[0].StoredFunctions.Should().BeEmpty();

        rungs[1].ArchiveRtId.Should().Be(daily);
        rungs[1].IsBase.Should().BeFalse("a rollup is never a base rung");
        rungs[1].Status.Should().Be(CkArchiveStatus.Created,
            "the family lists a rung whatever its lifecycle status");
        rungs[1].BucketSizeMs.Should().Be((long)TimeSpan.FromDays(1).TotalMilliseconds);
        rungs[1].Alignment.Should().Be(BucketAlignment.CalendarDay,
            "the rung reports the alignment it was declared with, not FixedSize");
        var declaredFunctions = new[] { CkRollupFunction.Sum, CkRollupFunction.Max };
        rungs[1].StoredFunctions.Should().BeEquivalentTo(declaredFunctions,
            "stored functions are the DISTINCT functions over the rung's aggregation specs");
    }

    /// <summary>
    /// The family coverage of an archive as the host serves it — the same
    /// <see cref="IArchiveFamilyCoverageService"/> instance behind <c>coverageFor</c> and
    /// <c>GET archives/{rtId}/coverage</c> (both projections are asserted in
    /// <see cref="RollupMultiSourceTests"/>).
    /// </summary>
    private async Task<IReadOnlyList<ArchiveCoverageRung>> FamilyAsync(OctoObjectId archiveRtId)
    {
        var tenantContext = await _builder.TenantAsync();
        var service = tenantContext.GetArchiveFamilyCoverageService();
        service.Should().NotBeNull("stream data is enabled for the fixture's tenant");
        return await service!.GetFamilyCoverageAsync(archiveRtId, TestContext.Current.CancellationToken);
    }
}
