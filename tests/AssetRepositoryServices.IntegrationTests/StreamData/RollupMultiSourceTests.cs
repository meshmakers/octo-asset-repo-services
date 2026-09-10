using System.Globalization;
using System.Text.Json;
using FakeItEasy;
using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Controllers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
/// AB#5157 — a rollup that aggregates from SEVERAL time-disjoint source archives, observed through
/// the two read surfaces the studio and the CLI use (<c>rollupsFor</c> / <c>coverageFor</c> over
/// GraphQL, <c>GET archives/{rtId}/rollups</c> / <c>.../coverage</c> over REST) and through the
/// generic CK mutation that may still re-declare the sources while the rollup is <c>Created</c>.
/// <para>
/// Membership is <c>HasSource</c> — the rollup appears under EVERY declared source whatever validity
/// span the reference carries — and the deprecated single-source projection is derived, not stored:
/// non-null only for exactly one unbounded source, null for a multi-source rollup and for a single
/// source carrying a span.
/// </para>
/// <para>
/// The helpers below (<see cref="CreateRawArchiveAsync"/>, <see cref="InsertPointsAsync"/>,
/// <see cref="CreateRollupAsync"/>, <see cref="ActivateAsync"/>, <see cref="TickAsync"/>,
/// <see cref="RewindAsync"/>, <see cref="RefreshAsync"/>, <see cref="NewController"/>) build a
/// complete multi-source ladder on the system tenant and are meant to be reused by the aggregation
/// facts that join this class later — extend, do not duplicate.
/// </para>
/// </summary>
[Collection(StreamDataMutatingCollection.Name)]
public class RollupMultiSourceTests(StreamDataFixture fixture, ITestOutputHelper output)
{
    /// <summary>Shared archive / rollup / CrateDB primitives (see <see cref="MultiSourceArchiveBuilder"/>).</summary>
    private readonly MultiSourceArchiveBuilder _builder = new(fixture);

    private static readonly TimeSpan OneHour = TimeSpan.FromHours(1);

    /// <summary>Bucket-aligned anchor of the synthetic multi-source history (1 h buckets).</summary>
    private static readonly DateTime H0 = new(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Cutover between the legacy source and the native source in the ladder fixtures.</summary>
    private static readonly DateTime Cutover = H0.AddHours(3);

    #region Listing

    [Fact]
    public async Task RollupsFor_ListsTheRollupUnderEveryDeclaredSource_WithNoDerivedScalar()
    {
        fixture.OutputHelper = output;
        var legacy = await CreateRawArchiveAsync("ListLegacy");
        var native = await CreateRawArchiveAsync("ListNative");

        var rollupRtId = await CreateRollupAsync("ListMultiSource",
        [
            new RollupSourceReference(legacy, ValidTo: Cutover),
            new RollupSourceReference(native, ValidFrom: Cutover),
        ]);

        foreach (var source in new[] { legacy, native })
        {
            var rollup = await RollupsForAsync(source, rollupRtId);
            rollup.Should().NotBeNull($"the rollup declares {source} among its sources");

            rollup!["sourceArchiveRtId"]!.Type.Should().Be(JTokenType.Null,
                "the deprecated projection is undefined for a multi-source rollup");

            var sources = (JArray)rollup["sources"]!;
            sources.Should().HaveCount(2);
            sources[0]["sourceArchiveRtId"]!.Value<string>().Should().Be(legacy.ToString());
            sources[0]["validFrom"]!.Type.Should().Be(JTokenType.Null);
            sources[0]["validTo"]!.Value<DateTime>().Should().Be(Cutover);
            sources[1]["sourceArchiveRtId"]!.Value<string>().Should().Be(native.ToString());
            sources[1]["validFrom"]!.Value<DateTime>().Should().Be(Cutover);
            sources[1]["validTo"]!.Type.Should().Be(JTokenType.Null);
        }
    }

    [Fact]
    public async Task RollupsFor_DerivesTheScalar_OnlyForExactlyOneUnboundedSource()
    {
        fixture.OutputHelper = output;
        var unbounded = await CreateRawArchiveAsync("ListUnbounded");
        var spanned = await CreateRawArchiveAsync("ListSpanned");

        var unboundedRollup = await CreateRollupAsync("ListSingleUnbounded",
            [new RollupSourceReference(unbounded)]);
        var spannedRollup = await CreateRollupAsync("ListSingleSpanned",
            [new RollupSourceReference(spanned, ValidFrom: H0)]);

        var listed = await RollupsForAsync(unbounded, unboundedRollup);
        listed!["sourceArchiveRtId"]!.Value<string>().Should().Be(unbounded.ToString());
        ((JArray)listed["sources"]!).Should().ContainSingle();

        var spannedListed = await RollupsForAsync(spanned, spannedRollup);
        spannedListed!["sourceArchiveRtId"]!.Type.Should().Be(JTokenType.Null,
            "a single source carrying a validity span is NOT the legacy single-unbounded shape");
        ((JArray)spannedListed["sources"]!)[0]["validFrom"]!.Value<DateTime>().Should().Be(H0);
    }

    [Fact]
    public async Task Rest_ListRollupsForArchive_CarriesEverySourceWithItsSpan()
    {
        fixture.OutputHelper = output;
        var legacy = await CreateRawArchiveAsync("RestLegacy");
        var native = await CreateRawArchiveAsync("RestNative");
        var rollupRtId = await CreateRollupAsync("RestMultiSource",
        [
            new RollupSourceReference(legacy, ValidTo: Cutover),
            new RollupSourceReference(native, ValidFrom: Cutover),
        ]);

        var controller = NewController();
        var response = await controller.ListRollupsForArchive(fixture.StreamDataTenantId, native.ToString());

        var rows = OkValue<IReadOnlyList<RollupArchiveInfoRestDto>>(response);
        var row = rows.Single(r => r.RtId == rollupRtId.ToString());

        row.SourceArchiveRtId.Should().BeNull("the deprecated REST projection mirrors SingleUnboundedSourceRtId");
        row.Status.Should().Be("Created");
        row.BucketSizeMs.Should().Be((long)OneHour.TotalMilliseconds);
        row.Sources.Should().HaveCount(2);
        row.Sources[0].Should().Be(new RollupSourceRestDto(legacy.ToString(), null, Cutover));
        row.Sources[1].Should().Be(new RollupSourceRestDto(native.ToString(), Cutover, null));
    }

    [Fact]
    public async Task Coverage_ReportsTheQueriedArchiveFirst_ThenItsRollup_OverRestAndGraphQl()
    {
        fixture.OutputHelper = output;
        var source = await CreateRawArchiveAsync("CoverageSource");
        var first = H0.AddHours(1);
        var last = H0.AddHours(4);
        await InsertPointsAsync(source, first, last);

        var rollupRtId = await CreateRollupAsync("CoverageRollup", [new RollupSourceReference(source)]);
        await ActivateAsync(rollupRtId);

        // ---- REST ----
        var controller = NewController();
        var rest = OkValue<IReadOnlyList<ArchiveCoverageRestDto>>(
            await controller.GetArchiveCoverage(
                fixture.StreamDataTenantId, source.ToString(), TestContext.Current.CancellationToken));

        rest.Should().HaveCount(2, "the queried archive comes first, then its transitive dependents");
        var baseRung = rest[0];
        baseRung.ArchiveRtId.Should().Be(source.ToString());
        baseRung.IsBase.Should().BeTrue();
        baseRung.Status.Should().Be("Activated");
        baseRung.BucketSizeMs.Should().BeNull("a raw archive has no declared grain");
        baseRung.BucketAlignment.Should().Be("FixedSize");
        baseRung.StoredFunctions.Should().BeEmpty();
        baseRung.AvailableFrom.Should().Be(first, "coverage is MEASURED from the rows, not declared");
        baseRung.AvailableTo.Should().Be(last);

        var rollupRung = rest[1];
        rollupRung.ArchiveRtId.Should().Be(rollupRtId.ToString());
        rollupRung.IsBase.Should().BeFalse();
        rollupRung.Status.Should().Be("Activated");
        rollupRung.BucketSizeMs.Should().Be((long)OneHour.TotalMilliseconds);
        rollupRung.BucketAlignment.Should().Be("FixedSize");
        rollupRung.StoredFunctions.Should().Equal("Sum");
        rollupRung.AvailableFrom.Should().BeNull("no bucket has been aggregated yet");
        rollupRung.AvailableTo.Should().BeNull();

        // ---- GraphQL: same rungs, camelCase names, CONSTANT_CASE alignment enum ----
        var graphQl = await ExecuteAsync(@"
            query ($rtId: OctoObjectId!) {
              streamData {
                coverageFor(rtId: $rtId) {
                  archiveRtId isBase status bucketSizeMs bucketAlignment storedFunctions
                  availableFrom availableTo
                }
              }
            }", new { rtId = source.ToString() });

        var rungs = (JArray)JObject.Parse(fixture.SerializeGraphQl(graphQl))
            .SelectToken("data.streamData.coverageFor")!;
        rungs.Should().HaveCount(2);
        rungs[0]["archiveRtId"]!.Value<string>().Should().Be(source.ToString());
        rungs[0]["bucketAlignment"]!.Value<string>().Should().Be("FIXED_SIZE");
        rungs[0]["availableFrom"]!.Value<DateTime>().Should().Be(first);
        rungs[1]["archiveRtId"]!.Value<string>().Should().Be(rollupRtId.ToString());
        rungs[1]["storedFunctions"]!.Values<string>().Should().Equal("SUM");
        rungs[1]["availableFrom"]!.Type.Should().Be(JTokenType.Null);
    }

    [Fact]
    public async Task Coverage_IsEmpty_ForAnUnknownArchive()
    {
        fixture.OutputHelper = output;
        var controller = NewController();

        var rest = OkValue<IReadOnlyList<ArchiveCoverageRestDto>>(
            await controller.GetArchiveCoverage(
                fixture.StreamDataTenantId, OctoObjectId.GenerateNewId().ToString(),
                TestContext.Current.CancellationToken));

        rest.Should().BeEmpty("an unknown rtId is an empty family, not an error");
    }

    [Fact]
    public async Task TC_API_12_SourcesRewrittenByTheGenericMutationWhileCreated_AreTheOnesActivationUses()
    {
        // The rollup is created single-source through createRollupArchive, then re-declared through
        // the generic CK entity mutation (what an operator does in the studio's entity editor) while
        // it is still Created. Activation must validate and use the NEW list, and both sources must
        // list the rollup afterwards.
        fixture.OutputHelper = output;
        var legacy = await CreateRawArchiveAsync("RedeclareLegacy");
        var native = await CreateRawArchiveAsync("RedeclareNative");
        var rollupRtId = await CreateRollupAsync("Redeclare", [new RollupSourceReference(legacy)]);

        var rollupCkTypeId = await RollupArchiveCkTypeIdAsync(rollupRtId);
        var update = await ExecuteAsync(@"
            mutation ($entities: [RtEntityUpdate!]!) {
              runtime { runtimeEntities { update(entities: $entities) { rtId } } }
            }",
            new
            {
                entities = new[]
                {
                    new
                    {
                        rtId = rollupRtId.ToString(),
                        item = new
                        {
                            ckTypeId = rollupCkTypeId,
                            attributes = new object[]
                            {
                                new
                                {
                                    attributeName = "sources",
                                    value = new object[]
                                    {
                                        new { sourceArchiveRtId = legacy.ToString(), validTo = Cutover },
                                        new { sourceArchiveRtId = native.ToString(), validFrom = Cutover },
                                    },
                                },
                            },
                        },
                    },
                },
            });
        update.Errors.Should().BeNullOrEmpty();

        var rewritten = await LoadRollupAsync(rollupRtId);
        rewritten.Sources.Should().HaveCount(2, "the generic mutation is the second write path onto Sources");

        await ActivateAsync(rollupRtId);

        var activated = await LoadRollupAsync(rollupRtId);
        activated.Status.Should().Be(CkArchiveStatus.Activated);
        activated.Sources.Select(s => s.SourceArchiveRtId).Should().Equal(legacy, native);
        activated.SourceForBucket(H0, H0.AddHours(1))!.SourceArchiveRtId.Should().Be(legacy);
        activated.SourceForBucket(Cutover, Cutover.AddHours(1))!.SourceArchiveRtId.Should().Be(native);

        (await RollupsForAsync(native, rollupRtId)).Should().NotBeNull(
            "the newly declared source now lists the rollup too");
        (await RollupsForAsync(legacy, rollupRtId)).Should().NotBeNull();
    }

    #endregion

    #region Aggregation

    // ── The AC1 ladder ────────────────────────────────────────────────────────────────────────
    //
    //   legacy daily TIME-RANGE archive ──[validTo 2025-10-01)──┐
    //                                                           ├──> daily rung D ──> monthly ──> yearly
    //   native base ──> hourly rung H   ──[validFrom 2025-10-01)┘
    //
    // AC1 verbatim: the legacy history is a BASE archive and the native history a ROLLUP, both
    // sources of one rung declared with a single LOGICAL aggregation spec ("Voltage", Sum). Since
    // AB#5157 the engine resolves that spec per source — the time-range archive declares the CK
    // path itself and is read as SUM("voltage"), the hourly rung stores the same logical
    // aggregation and is read as SUM("voltage_sum"), its own generated target column.
    //
    // Both halves deliberately also hold rows OUTSIDE the span they are authoritative for, so every
    // read of D doubles as the negative proof that a bucket never sees the wrong source. The control
    // ladder (one archive holding the concatenated data + a single-source rung) is built at the SAME
    // grain as D — the two sources differ in grain, so "the concatenated data" can only be expressed
    // at the rung's own resolution.

    /// <summary>Cutover of the AC1 ladder: legacy is authoritative before, native from here on.</summary>
    private static readonly DateTime Ac1Cutover = new(2025, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>First legacy day (five daily totals: 2025-09-26 … 2025-09-30).</summary>
    private static readonly DateTime Ac1FirstDay = new(2025, 9, 26, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Exclusive end of the native half (two native days: 2025-10-01, 2025-10-02).</summary>
    private static readonly DateTime Ac1NativeEnd = new(2025, 10, 3, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The seven days the daily rung must materialise — five legacy, two native.</summary>
    private static readonly DateTime[] Ac1Days =
    [
        Ac1FirstDay, Ac1FirstDay.AddDays(1), Ac1FirstDay.AddDays(2), Ac1FirstDay.AddDays(3),
        Ac1FirstDay.AddDays(4), Ac1Cutover, Ac1Cutover.AddDays(1),
    ];

    /// <summary>Legacy daily totals for 2025-09-26 … 2025-09-30, then the two native days (24 × 1.0).</summary>
    private static readonly double[] Ac1DailyValues = [10d, 20d, 30d, 40d, 50d, 24d, 24d];

    /// <summary>The legacy window deliberately written AFTER the legacy source's ValidTo.</summary>
    private static readonly DateTime Ac1PoisonLegacyDay = new(2025, 10, 5, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The native hours deliberately written BEFORE the native source's ValidFrom.</summary>
    private static readonly DateTime Ac1PoisonNativeDay = new(2025, 9, 29, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Comparison tolerance for aggregate values. The catalogue's "identical" wording is executed
    /// against a stated tolerance rather than bit equality (test catalogue, ambiguity notes); these
    /// sums of doubles are exact at this magnitude, so the tolerance only absorbs a future change in
    /// the SQL's accumulation order.
    /// </summary>
    private const double Tolerance = 1e-9;

    /// <summary>The archives of the AC1 ladder, built once for the whole class.</summary>
    private sealed record Ac1Ladder(
        OctoObjectId LegacyWindows,
        OctoObjectId NativeBase,
        OctoObjectId Hourly,
        OctoObjectId Daily,
        OctoObjectId Monthly,
        OctoObjectId Yearly,
        OctoObjectId ControlBase,
        OctoObjectId ControlDaily,
        OctoObjectId Series);

    private static readonly SemaphoreSlim Ac1Gate = new(1, 1);
    private static Ac1Ladder? _ac1;

    [Fact]
    public async Task TC_E2E_01_DailyRungOverLegacyAndNative_IsOneContinuousSeries_EqualToTheConcatenatedSingleSourceRollup()
    {
        fixture.OutputHelper = output;
        var ladder = await Ac1Async();

        var daily = await _builder.ReadBucketsAsync(ladder.Daily);

        daily.Select(b => b.WindowStart).Should().Equal(Ac1Days,
            "exactly one row per day from the first legacy day to the last native day — the cutover " +
            "adds neither a gap nor a duplicate, and no bucket is produced where no source holds data");

        for (var i = 0; i < Ac1Days.Length; i++)
        {
            daily[i].Value.Should().NotBeNull();
            daily[i].Value!.Value.Should().BeApproximately(Ac1DailyValues[i], Tolerance,
                $"day {Ac1Days[i]:yyyy-MM-dd} is served by exactly one source");
        }

        // The same series aggregated from ONE archive holding the concatenated data.
        var control = await _builder.ReadBucketsAsync(ladder.ControlDaily);
        control.Select(b => b.WindowStart).Should().Equal(daily.Select(b => b.WindowStart));
        for (var i = 0; i < control.Count; i++)
        {
            daily[i].Value!.Value.Should().BeApproximately(control[i].Value!.Value, Tolerance,
                "a multi-source rung must equal a single-source rung over the concatenated data");
        }
    }

    [Fact]
    public async Task TC_AGG_01_TC_AGG_02_TC_AGG_13_EachBucketIsServedByExactlyOneSource_AndOutOfSpanRowsAreIgnored()
    {
        fixture.OutputHelper = output;
        var ladder = await Ac1Async();

        var daily = (await _builder.ReadBucketsAsync(ladder.Daily))
            .ToDictionary(b => b.WindowStart, b => b.Value);

        // ValidTo is EXCLUSIVE: the last bucket that ENDS at the cutover is still the legacy
        // source's; ValidFrom is INCLUSIVE: the bucket that STARTS at the cutover is already native.
        daily[Ac1Cutover.AddDays(-1)]!.Value.Should().BeApproximately(50d, Tolerance,
            "[2025-09-30, 2025-10-01) ends at the cutover and is therefore still the legacy source's");
        daily[Ac1Cutover]!.Value.Should().BeApproximately(24d, Tolerance,
            "[2025-10-01, 2025-10-02) starts at the cutover and is therefore the native source's");

        // The legacy archive also holds a window well after its ValidTo, and the native rung holds
        // hours well before its ValidFrom. Neither may reach the daily rung.
        daily.Should().NotContainKey(Ac1PoisonLegacyDay,
            "a legacy row outside the legacy span belongs to no bucket — the native source owns that range");
        daily[Ac1PoisonNativeDay]!.Value.Should().BeApproximately(40d, Tolerance,
            "the native rows inside the legacy span must not be added to the legacy day's total");

        // Both out-of-span rows really are in their source archives — the rung ignores them, it does
        // not merely fail to see missing data.
        (await _builder.ScalarLongAsync(
                $"SELECT count(*) FROM {_builder.QualifiedTable(ladder.LegacyWindows)} " +
                $"WHERE \"window_start\"::bigint = {ToEpochMs(Ac1PoisonLegacyDay)}"))
            .Should().Be(1, "the out-of-span legacy day is stored in the legacy time-range archive");
        (await _builder.ScalarLongAsync(
                $"SELECT count(*) FROM {_builder.QualifiedTable(ladder.Hourly)} " +
                $"WHERE \"window_start\"::bigint >= {ToEpochMs(Ac1PoisonNativeDay)} " +
                $"AND \"window_start\"::bigint < {ToEpochMs(Ac1PoisonNativeDay.AddDays(1))}"))
            .Should().Be(2, "the out-of-span native hours are materialised on the hourly rung");
    }

    [Fact]
    public async Task TC_E2E_02_TC_E2E_05_MonthlyAndYearlyRungsOnTopOfTheMultiSourceRung_CoverTheFullHistory()
    {
        fixture.OutputHelper = output;
        var ladder = await Ac1Async();

        // The monthly rung's only configuration is "source = the daily rung" — it knows nothing
        // about the cutover, yet its September bucket is legacy history and its October bucket is
        // native history.
        var monthly = await _builder.ReadBucketsAsync(ladder.Monthly);
        monthly.Select(b => b.WindowStart).Should().Equal(
        [
            new DateTime(2025, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2025, 10, 1, 0, 0, 0, DateTimeKind.Utc),
        ]);
        monthly[0].Value!.Value.Should().BeApproximately(150d, Tolerance, "September = the five legacy days");
        monthly[1].Value!.Value.Should().BeApproximately(48d, Tolerance, "October = the two native days");

        var yearly = await _builder.ReadBucketsAsync(ladder.Yearly);
        yearly.Should().ContainSingle();
        yearly[0].WindowStart.Should().Be(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        yearly[0].Value!.Value.Should().BeApproximately(198d, Tolerance,
            "the yearly rung spans the cutover through two single-source rungs above the multi-source one");
    }

    [Fact]
    public async Task TC_AGG_14_EveryQueryFlavourOverTheMultiSourceRollup_ReadsOneContinuousSeries()
    {
        fixture.OutputHelper = output;
        var ladder = await Ac1Async();

        var from = Ac1FirstDay.ToString("O");
        var to = Ac1NativeEnd.ToString("O");
        var column = MultiSourceArchiveBuilder.RollupColumn;
        var total = Ac1DailyValues.Sum();

        // simple — one continuous series read from ONE archive, no client-side merge.
        var simple = await QueryValuesAsync($@"
            {{ streamData {{ transientStreamDataQuery {{ simple(
                 archiveRtId: ""{ladder.Daily}"" columnPaths: [""{column}""]
                 arg: {{ from: ""{from}"", to: ""{to}"", queryMode: DEFAULT }} first: 100)
               {{ items {{ rows(first: 100) {{ totalCount items {{ cells(first: 5) {{ items {{ attributePath value }} }} }} }} }} }}
            }} }} }}", cell => cell["attributePath"]!.Value<string>() == column);
        simple.Should().HaveCount(Ac1Days.Length, "the rung is read as one archive across the cutover");
        simple.Sum().Should().BeApproximately(total, Tolerance);

        // aggregation — one scalar over the whole (multi-source) history.
        var aggregation = await QueryValuesAsync($@"
            {{ streamData {{ transientStreamDataQuery {{ aggregation(
                 archiveRtId: ""{ladder.Daily}""
                 columnPaths: [{{ attributePath: ""{column}"", aggregationType: SUM }}]
                 arg: {{ from: ""{from}"", to: ""{to}"", queryMode: DEFAULT }} first: 10)
               {{ items {{ rows(first: 10) {{ items {{ cells(first: 5) {{ items {{ value }} }} }} }} }} }}
            }} }} }}");
        aggregation.Should().ContainSingle().Which.Should().BeApproximately(total, Tolerance);

        // grouping aggregation — grouped by the series, still one row.
        var grouping = await QueryValuesAsync($@"
            {{ streamData {{ transientStreamDataQuery {{ groupingAggregation(
                 archiveRtId: ""{ladder.Daily}"" groupByColumnPaths: [""rtId""]
                 columnPaths: [{{ attributePath: ""{column}"", aggregationType: SUM }}]
                 arg: {{ from: ""{from}"", to: ""{to}"", queryMode: DEFAULT }} first: 10)
               {{ items {{ rows(first: 10) {{ items {{ cells(first: 5) {{ items {{ attributePath value }} }} }} }} }} }}
            }} }} }}", cell => cell["attributePath"]!.Value<string>() != "rtId");
        grouping.Should().ContainSingle().Which.Should().BeApproximately(total, Tolerance);

        // downsampling — the range split into bins spanning the cutover.
        var downsampling = await QueryValuesAsync($@"
            {{ streamData {{ transientStreamDataQuery {{ downsampling(
                 archiveRtId: ""{ladder.Daily}""
                 columnPaths: [{{ attributePath: ""{column}"", aggregationType: SUM }}]
                 limit: 7 from: ""{from}"" to: ""{to}"" first: 100)
               {{ items {{ rows(first: 100) {{ items {{ cells(first: 5) {{ items {{ value }} }} }} }} }} }}
            }} }} }}");
        downsampling.Sum().Should().BeApproximately(total, Tolerance,
            "downsampling redistributes the same values into bins — nothing is lost or duplicated at the cutover");
    }

    [Fact]
    public async Task TC_AGG_07_OneLogicalSpec_ReadsTheDeclaredColumnOnOneSource_AndTheChildAggregationOnTheOther()
    {
        fixture.OutputHelper = output;
        var ladder = await Ac1Async();

        // The two sources of the daily rung capture the same logical quantity under DIFFERENT
        // physical column names: the time-range archive declares the CK attribute path (stored as
        // "voltage"), the hourly rung its own generated aggregate column ("voltage_sum"). One
        // logical spec ("Voltage", Sum) is resolved against each of them separately.
        var archives = (await TenantAsync()).GetArchiveRuntimeStore();
        var legacyPaths = (await archives.GetAsync(ladder.LegacyWindows))!.Columns.Select(c => c.Path).ToList();
        legacyPaths.Should().Contain(MultiSourceArchiveBuilder.VoltagePath,
            "a base archive declares the CK attribute path, read as its storage column 'voltage'");
        legacyPaths.Should().NotContain(MultiSourceArchiveBuilder.RollupColumn,
            "a base archive never carries a rollup's generated column name");

        var hourlyPaths = (await archives.GetAsync(ladder.Hourly))!.Columns.Select(c => c.Path).ToList();
        hourlyPaths.Should().Contain(MultiSourceArchiveBuilder.RollupColumn,
            "a rollup source declares its generated physical columns");
        hourlyPaths.Should().NotContain(MultiSourceArchiveBuilder.VoltagePath,
            "…and never the CK path the spec names — which is why the verbatim rule cannot serve it");

        var daily = (await _builder.ReadBucketsAsync(ladder.Daily))
            .ToDictionary(b => b.WindowStart, b => b.Value);

        daily[Ac1FirstDay]!.Value.Should().BeApproximately(10d, Tolerance,
            "the legacy day is the time-range archive's own window value, read from 'voltage'");
        daily[Ac1Cutover]!.Value.Should().BeApproximately(24d, Tolerance,
            "the native day is the sum of the hourly rung's 24 buckets, read from 'voltage_sum'");
        daily.Values.Should().OnlyContain(v => v != null,
            "both spans populate the SAME target column of the rung — neither side leaves it empty");

        // A source that carries neither the declared path nor a matching child aggregation is
        // refused at activation, and the exception names exactly that source.
        var withColumn = await CreateRawArchiveAsync("PathPresent");
        var withoutColumn = await CreateRawArchiveAsync("PathMissing", "Current");
        var mismatched = await CreateRollupAsync("PathMismatch",
        [
            new RollupSourceReference(withColumn, ValidTo: Cutover),
            new RollupSourceReference(withoutColumn, ValidFrom: Cutover),
        ]);

        var act = async () => await ActivateAsync(mismatched);
        (await act.Should().ThrowAsync<RollupSourcePathMissingException>())
            .Which.Message.Should().Contain(withoutColumn.ToString(),
                "the operator must learn WHICH source lacks the column");
    }

    [Fact]
    public async Task TC_AGG_15_ARollupSourceAggregatingAnotherAttributeUnderTheSameColumnName_IsRefusedAtActivation()
    {
        // AB#5157 AC2, the shape the feature exists for. The legacy half carries an attribute the
        // native rung never aggregated, and both the rung and that native rung store into the same
        // column name — a pinned TargetColumnName is a storage decision and says nothing about the
        // attribute behind it. Resolving on the stored name let this activate and backfill, so the
        // buckets before the cutover held one quantity and the ones after held another, in a single
        // column, with no warning anywhere. It must be refused, naming the source at fault.
        fixture.OutputHelper = output;

        // Legacy half: a base archive that declares Current and nothing else.
        var legacy = await CreateRawArchiveAsync("MeaningLegacy", "Current");

        // Native half: an hourly rung that only ever aggregated Voltage, stored as "voltage_sum".
        var nativeBase = await CreateRawArchiveAsync("MeaningNativeBase");
        var hourly = await CreateRollupAsync("MeaningNativeHourly", [new RollupSourceReference(nativeBase)]);
        await ActivateAsync(hourly);

        // The daily rung aggregates Current — and happens to pin the very column name the hourly
        // rung writes. Its cutover sits on a day boundary, as a daily rung's spans must.
        var dailyCutover = new DateTime(2026, 4, 2, 0, 0, 0, DateTimeKind.Utc);
        var daily = await _builder.CreateRollupAsync("MeaningDaily",
            [
                new RollupSourceReference(legacy, ValidTo: dailyCutover),
                new RollupSourceReference(hourly, ValidFrom: dailyCutover),
            ],
            TimeSpan.FromDays(1),
            [new CkRollupAggregationSpec("Current", CkRollupFunction.Sum, MultiSourceArchiveBuilder.RollupColumn)]);

        var act = async () => await ActivateAsync(daily);
        (await act.Should().ThrowAsync<RollupSourcePathMissingException>())
            .Which.Message.Should().Contain(hourly.ToString(),
                "the rollup source cannot serve Current, whatever column name the two happen to share");
    }

    [Fact]
    public async Task AMixedBaseAndRollupSourceLadder_Activates_AndEqualsTheSingleSourceEquivalent()
    {
        // AB#5157 AC1 ("legacy daily time-range archive + hourly rollup") and the sbeg scenario
        // ("legacy quarterly time-range archive + monthly rollup") both declare ONE base archive and
        // ONE rollup as the sources of a rung. A base archive declares its columns by CK attribute
        // path (PascalCase, case-sensitively validated against the model at activation); a rollup
        // declares its columns by their PHYSICAL storage name, which the column generator always
        // lower-cases. Since AB#5157 the rung's aggregation spec stays LOGICAL and is resolved per
        // source: verbatim against the base archive's declared path, and against the rollup through
        // its own child aggregation with the same function and normalised path. Before that fix no
        // single spec could satisfy both source kinds and this shape could not be activated at all.
        fixture.OutputHelper = output;
        var series = OctoObjectId.GenerateNewId();
        var day0 = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc);
        var cutover = day0.AddDays(2);
        var end = day0.AddDays(4);

        // Legacy half: a daily TIME-RANGE archive declaring the CK path, with one window past its
        // ValidTo that the native source owns.
        var legacy = await _builder.CreateTimeRangeArchiveAsync("MixedLegacy", TimeSpan.FromDays(1));
        await _builder.InsertWindowsAsync(legacy, series,
        [
            (day0, day0.AddDays(1), 10d),
            (day0.AddDays(1), cutover, 20d),
            (cutover, cutover.AddDays(1), 999d),
        ]);

        // Native half: a raw archive with an hourly rung over it — 24 buckets of 1.0 per day.
        var nativeBase = await CreateRawArchiveAsync("MixedNativeBase");
        await _builder.InsertPointsAsync(nativeBase, series,
            Enumerable.Range(0, 48).Select(i => (cutover.AddHours(i), 1d)));
        var hourly = await _builder.CreateRollupAsync("MixedHourly",
            [new RollupSourceReference(nativeBase)], OneHour);
        await ActivateAsync(hourly);
        await _builder.RecomputeAsync(hourly, cutover, end);

        var mixed = await _builder.CreateRollupAsync("MixedDaily",
            [
                new RollupSourceReference(legacy, ValidTo: cutover),
                new RollupSourceReference(hourly, ValidFrom: cutover),
            ],
            TimeSpan.FromDays(1), MultiSourceArchiveBuilder.LogicalSum, BucketAlignment.CalendarDay);

        await ActivateAsync(mixed);
        (await LoadRollupAsync(mixed)).Status.Should().Be(CkArchiveStatus.Activated,
            "one logical spec resolves on the base archive verbatim and on the rollup through its "
            + "own child aggregation — the AC1 and sbeg shape activates");

        await _builder.RecomputeAsync(mixed, day0, end);

        // The single-source equivalent: ONE archive holding the concatenated daily totals, rolled
        // up at the same grain.
        var expected = new[] { 10d, 20d, 24d, 24d };
        var controlBase = await CreateRawArchiveAsync("MixedControlBase");
        await _builder.InsertPointsAsync(controlBase, series,
            expected.Select((value, i) => (day0.AddDays(i).AddHours(12), value)));
        var control = await _builder.CreateRollupAsync("MixedControlDaily",
            [new RollupSourceReference(controlBase)], TimeSpan.FromDays(1),
            bucketAlignment: BucketAlignment.CalendarDay);
        await ActivateAsync(control);
        await _builder.RecomputeAsync(control, day0, end);

        var buckets = await _builder.ReadBucketsAsync(mixed);
        buckets.Select(b => b.WindowStart).Should().Equal(
            [day0, day0.AddDays(1), cutover, cutover.AddDays(1)],
            "one continuous series across the cutover — neither a gap nor a duplicate");

        var controlBuckets = await _builder.ReadBucketsAsync(control);
        controlBuckets.Select(b => b.WindowStart).Should().Equal(buckets.Select(b => b.WindowStart));
        for (var i = 0; i < expected.Length; i++)
        {
            buckets[i].Value!.Value.Should().BeApproximately(expected[i], Tolerance);
            buckets[i].Value!.Value.Should().BeApproximately(controlBuckets[i].Value!.Value, Tolerance,
                "a mixed base + rollup rung equals a single-source rung over the concatenated data");
        }

        buckets[1].Value!.Value.Should().BeApproximately(20d, Tolerance,
            "the bucket that ENDS at the cutover reads ONLY the legacy archive — ValidTo is exclusive");
        buckets[2].Value!.Value.Should().BeApproximately(24d, Tolerance,
            "the bucket that STARTS at the cutover reads ONLY the native rollup — the legacy window "
            + "written into that day (999) never reaches it");

        // The mirror image: a PHYSICAL-name spec still resolves on a rollup source only. The base
        // archive declares no such column and has no child aggregation to fall back on, so the
        // chained style cannot serve a mixed rung — the exception names the base archive.
        var mirrored = await _builder.CreateRollupAsync("MixedDailyMirrored",
            [
                new RollupSourceReference(legacy, ValidTo: cutover),
                new RollupSourceReference(hourly, ValidFrom: cutover),
            ],
            TimeSpan.FromDays(1), MultiSourceArchiveBuilder.CascadeSum, BucketAlignment.CalendarDay);

        var mirroredAct = async () => await ActivateAsync(mirrored);
        (await mirroredAct.Should().ThrowAsync<RollupSourcePathMissingException>())
            .Which.Message.Should().Contain(legacy.ToString());
    }

    [Fact]
    public async Task TC_AGG_03_TC_AGG_04_ABucketNoSpanCoversStaysEmpty_AndTheWatermarkMovesOn()
    {
        fixture.OutputHelper = output;
        var before = await CreateRawArchiveAsync("GapBefore");
        var after = await CreateRawArchiveAsync("GapAfter");
        var gapStart = H0.AddHours(2);
        var gapEnd = H0.AddHours(4);

        await _builder.InsertPointsAsync(before, OctoObjectId.GenerateNewId(),
            [(H0, 1d), (H0.AddHours(1), 1d)]);
        await _builder.InsertPointsAsync(after, OctoObjectId.GenerateNewId(),
            [(gapEnd, 3d), (gapEnd.AddHours(1), 3d)]);

        var rollupRtId = await CreateRollupAsync("GapLadder",
        [
            new RollupSourceReference(before, ValidTo: gapStart),
            new RollupSourceReference(after, ValidFrom: gapEnd),
        ]);
        await ActivateAsync(rollupRtId);
        await RewindAsync(rollupRtId, H0);
        (await TickAsync(rollupRtId)).Should().BeGreaterThan(4, "the tick walks past the gap, it does not stall");

        var buckets = await _builder.ReadBucketsAsync(rollupRtId);
        buckets.Select(b => b.WindowStart).Should().Equal([H0, H0.AddHours(1), gapEnd, gapEnd.AddHours(1)]);
        buckets.Should().OnlyContain(b => b.Value != null,
            "an uncovered bucket produces NO row at all — not a zero and not a NULL placeholder");

        (await LoadRollupAsync(rollupRtId)).LastAggregatedBucketEnd.Should().BeAfter(gapEnd,
            "the watermark advanced across the deliberately uncovered range");
    }

    [Fact]
    public async Task TC_AGG_05_ASourceWindowNotFullyContainedInABucketContributesToNoBucket()
    {
        fixture.OutputHelper = output;
        var legacy = await _builder.CreateTimeRangeArchiveAsync("StraddleLegacy", OneHour);
        var native = await CreateRawArchiveAsync("StraddleNative");
        var series = OctoObjectId.GenerateNewId();

        await _builder.InsertWindowsAsync(legacy, series,
        [
            // Straddles the [H0, H0+1h) / [H0+1h, H0+2h) boundary — contained in neither.
            (H0.AddMinutes(30), H0.AddMinutes(90), 999d),
            (H0.AddHours(1), H0.AddHours(2), 7d),
        ]);
        await _builder.InsertPointsAsync(native, series, [(Cutover, 5d)]);

        var rollupRtId = await _builder.CreateRollupAsync("StraddleRollup",
            [
                new RollupSourceReference(legacy, ValidTo: Cutover),
                new RollupSourceReference(native, ValidFrom: Cutover),
            ],
            OneHour);
        await ActivateAsync(rollupRtId);
        await RewindAsync(rollupRtId, H0);
        await TickAsync(rollupRtId);

        var buckets = await _builder.ReadBucketsAsync(rollupRtId);
        buckets.Select(b => b.WindowStart).Should().Equal([H0.AddHours(1), Cutover],
            "the straddling window belongs to no bucket — the fully-contained rule is unchanged by multi-source");
        buckets[0].Value!.Value.Should().BeApproximately(7d, Tolerance,
            "999 would show up here if a partially contained window leaked into the bucket");
        buckets[1].Value!.Value.Should().BeApproximately(5d, Tolerance);
    }

    [Fact]
    public async Task ThreeDisjointSources_EachServeExactlyTheBucketsInsideTheirOwnSpan()
    {
        fixture.OutputHelper = output;
        var first = await CreateRawArchiveAsync("TriFirst");
        var second = await CreateRawArchiveAsync("TriSecond");
        var third = await CreateRawArchiveAsync("TriThird");
        var firstCut = H0.AddHours(2);
        var secondCut = H0.AddHours(4);
        var series = OctoObjectId.GenerateNewId();

        // Every archive holds the WHOLE range; only the spans decide which bucket reads which.
        foreach (var (archive, value) in new[] { (first, 1d), (second, 2d), (third, 3d) })
        {
            await _builder.InsertPointsAsync(archive, series,
                Enumerable.Range(0, 6).Select(i => (H0.AddHours(i), value)));
        }

        var rollupRtId = await CreateRollupAsync("TriSource",
        [
            new RollupSourceReference(first, ValidTo: firstCut),
            new RollupSourceReference(second, ValidFrom: firstCut, ValidTo: secondCut),
            new RollupSourceReference(third, ValidFrom: secondCut),
        ]);
        await ActivateAsync(rollupRtId);
        await RewindAsync(rollupRtId, H0);
        await TickAsync(rollupRtId);

        var buckets = (await _builder.ReadBucketsAsync(rollupRtId)).Take(6).ToList();
        buckets.Select(b => b.Value!.Value).Should().Equal([1d, 1d, 2d, 2d, 3d, 3d],
            "each bucket carries exactly the value of the source whose span contains it");
    }

    [Fact]
    public async Task ARollupOnTopOfAMultiSourceRung_SeesOneContinuousSeriesBeneathIt()
    {
        fixture.OutputHelper = output;
        var (multiSource, _, _) = await BuildHourlyCutoverLadderAsync("Cascade", 1d, 3d);

        var twoHourly = await _builder.CreateRollupAsync("CascadeTop",
            [new RollupSourceReference(multiSource)], TimeSpan.FromHours(2),
            MultiSourceArchiveBuilder.CascadeSum);
        await ActivateAsync(twoHourly);
        await RewindAsync(twoHourly, H0);
        await TickAsync(twoHourly);

        var buckets = (await _builder.ReadBucketsAsync(twoHourly)).Take(3).ToList();
        buckets.Select(b => b.WindowStart).Should().Equal([H0, H0.AddHours(2), H0.AddHours(4)]);
        buckets.Select(b => b.Value!.Value).Should().Equal([2d, 4d, 6d],
            "the 2 h rung sums pairs of 1 h buckets, including the pair that straddles the cutover " +
            "(1+1 legacy, 1+3 across the cutover, 3+3 native)");
    }

    [Fact]
    public async Task FrozenUntil_BlocksBucketsOnBothSidesOfTheCutover_AndReleasingItProducesThemAll()
    {
        fixture.OutputHelper = output;
        var legacy = await CreateRawArchiveAsync("FreezeLegacy");
        var native = await CreateRawArchiveAsync("FreezeNative");
        var series = OctoObjectId.GenerateNewId();
        await _builder.InsertPointsAsync(legacy, series, [(H0, 1d), (H0.AddHours(1), 1d)]);
        await _builder.InsertPointsAsync(native, series, [(Cutover, 3d), (Cutover.AddHours(1), 3d)]);

        var rollupRtId = await CreateRollupAsync("FreezeLadder",
        [
            new RollupSourceReference(legacy, ValidTo: Cutover),
            new RollupSourceReference(native, ValidFrom: Cutover),
        ]);
        await ActivateAsync(rollupRtId);

        var lifecycle = (await TenantAsync()).GetRollupArchiveLifecycleService()!;
        await lifecycle.FreezeAsync(rollupRtId, H0.AddHours(5));
        await RewindAsync(rollupRtId, H0);
        await TickAsync(rollupRtId);

        (await _builder.ReadBucketsAsync(rollupRtId)).Should().BeEmpty(
            "a freeze preserves the frozen range as-is on BOTH sides of the cutover — which source " +
            "would serve a bucket is irrelevant while it is frozen");
        (await LoadRollupAsync(rollupRtId)).LastAggregatedBucketEnd.Should().BeOnOrAfter(H0.AddHours(5),
            "the watermark still catches up to the frozen-until boundary");

        await lifecycle.UnfreezeAsync(rollupRtId, acceptGaps: true);
        await RewindAsync(rollupRtId, H0);
        await TickAsync(rollupRtId);

        var buckets = (await _builder.ReadBucketsAsync(rollupRtId)).Take(4).ToList();
        buckets.Select(b => b.WindowStart).Should().Equal([H0, H0.AddHours(1), Cutover, Cutover.AddHours(1)]);
        buckets.Select(b => b.Value!.Value).Should().Equal([1d, 1d, 3d, 3d]);
    }

    [Fact]
    public async Task TC_REC_10_RewindingTheWatermark_ReproducesEveryBucketFromItsOwnSourceAcrossTheCutover()
    {
        fixture.OutputHelper = output;
        var (rollupRtId, _, _) = await BuildHourlyCutoverLadderAsync("Rewind", 1d, 3d);

        var before = await _builder.ReadBucketsAsync(rollupRtId);
        before.Take(6).Select(b => b.Value!.Value).Should().Equal([1d, 1d, 1d, 3d, 3d, 3d]);

        await RewindAsync(rollupRtId, H0);
        (await TickAsync(rollupRtId)).Should().BeGreaterThan(5, "the rewound range is re-processed");

        var after = await _builder.ReadBucketsAsync(rollupRtId);
        after.Select(b => b.WindowStart).Should().Equal(before.Select(b => b.WindowStart),
            "re-processing must not add or drop a bucket at the cutover");
        after.Select(b => b.Value).Should().Equal(before.Select(b => b.Value),
            "every bucket is re-produced from the same source and yields the same value");
    }

    [Fact]
    public async Task ALegacySingleSourceRollup_KeepsItsEndToEndBehaviour()
    {
        fixture.OutputHelper = output;
        var source = await CreateRawArchiveAsync("LegacySingle");
        var series = OctoObjectId.GenerateNewId();
        await _builder.InsertPointsAsync(source, series, [(H0, 2d), (H0.AddHours(1), 2d)]);

        var rollupRtId = await CreateRollupAsync("LegacySingleRollup", [new RollupSourceReference(source)]);
        await ActivateAsync(rollupRtId);
        await RewindAsync(rollupRtId, H0);
        await TickAsync(rollupRtId);

        var snapshot = await LoadRollupAsync(rollupRtId);
        snapshot.Sources.Should().ContainSingle();
        snapshot.SingleUnboundedSourceRtId.Should().Be(source,
            "one unbounded source is still projected onto the deprecated scalar");
        snapshot.SourceForBucket(H0, H0.AddHours(1))!.SourceArchiveRtId.Should().Be(source);

        var listed = await RollupsForAsync(source, rollupRtId);
        listed!["sourceArchiveRtId"]!.Value<string>().Should().Be(source.ToString());

        var buckets = (await _builder.ReadBucketsAsync(rollupRtId)).Take(2).ToList();
        buckets.Select(b => b.Value!.Value).Should().Equal([2d, 2d]);
    }

    [Fact]
    public async Task SourcesWithDifferentReferenceZones_AreBucketedByTheRungsOwnZone_AcrossADstChange()
    {
        fixture.OutputHelper = output;
        const string vienna = "Europe/Vienna";

        // 2025-11-01 00:00 Vienna = 2025-10-31T23:00Z (CET, after the 26 October DST change);
        // 2025-10-01 00:00 Vienna = 2025-09-30T22:00Z (CEST). Both are month boundaries of the TOP
        // rung's zone — the sources' own zones differ from it and from each other.
        var octoberStart = new DateTime(2025, 9, 30, 22, 0, 0, DateTimeKind.Utc);
        var novemberStart = new DateTime(2025, 10, 31, 23, 0, 0, DateTimeKind.Utc);

        var legacyBase = await CreateRawArchiveAsync("ZoneLegacyBase");
        var nativeBase = await CreateRawArchiveAsync("ZoneNativeBase");
        var series = OctoObjectId.GenerateNewId();
        await _builder.InsertPointsAsync(legacyBase, series,
            [(new DateTime(2025, 10, 29, 6, 0, 0, DateTimeKind.Utc), 5d)]);
        await _builder.InsertPointsAsync(nativeBase, series,
            [(new DateTime(2025, 11, 3, 6, 0, 0, DateTimeKind.Utc), 9d)]);

        // Two daily rungs with DIFFERENT reference zones — their own day boundaries differ by an hour.
        var legacyDaily = await _builder.CreateRollupAsync("ZoneLegacyDaily",
            [new RollupSourceReference(legacyBase)], TimeSpan.FromDays(1),
            bucketAlignment: BucketAlignment.CalendarDay, referenceTimeZone: null);
        var nativeDaily = await _builder.CreateRollupAsync("ZoneNativeDaily",
            [new RollupSourceReference(nativeBase)], TimeSpan.FromDays(1),
            bucketAlignment: BucketAlignment.CalendarDay, referenceTimeZone: vienna);
        await ActivateAsync(legacyDaily);
        await ActivateAsync(nativeDaily);
        await _builder.RecomputeAsync(legacyDaily,
            new DateTime(2025, 10, 29, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2025, 10, 30, 0, 0, 0, DateTimeKind.Utc));
        await _builder.RecomputeAsync(nativeDaily,
            new DateTime(2025, 11, 2, 23, 0, 0, DateTimeKind.Utc),
            new DateTime(2025, 11, 3, 23, 0, 0, DateTimeKind.Utc));

        (await _builder.ReadBucketsAsync(legacyDaily))[0].WindowStart.Should().Be(
            new DateTime(2025, 10, 29, 0, 0, 0, DateTimeKind.Utc), "a UTC-zoned rung breaks days at UTC midnight");
        (await _builder.ReadBucketsAsync(nativeDaily))[0].WindowStart.Should().Be(
            new DateTime(2025, 11, 2, 23, 0, 0, DateTimeKind.Utc), "a Vienna-zoned rung breaks days at local midnight");

        var monthly = await _builder.CreateRollupAsync("ZoneMonthly",
            [
                new RollupSourceReference(legacyDaily, ValidTo: novemberStart),
                new RollupSourceReference(nativeDaily, ValidFrom: novemberStart),
            ],
            TimeSpan.FromDays(28), MultiSourceArchiveBuilder.CascadeSum,
            BucketAlignment.CalendarMonth, vienna);
        await ActivateAsync(monthly);
        await _builder.RecomputeAsync(monthly, octoberStart, new DateTime(2025, 11, 30, 23, 0, 0, DateTimeKind.Utc));

        var buckets = await _builder.ReadBucketsAsync(monthly);
        buckets.Select(b => b.WindowStart).Should().Equal([octoberStart, novemberStart],
            "the rung's OWN reference zone decides the bucket boundaries — one before and one after " +
            "the DST change, both at local midnight, whatever zones its sources carry");
        buckets[0].Value!.Value.Should().BeApproximately(5d, Tolerance, "October is the legacy rung's");
        buckets[1].Value!.Value.Should().BeApproximately(9d, Tolerance, "November is the native rung's");
    }

    [Fact]
    public async Task TC_E2E_03_TC_E2E_04_SbegQuarterLadder_ActivatesWithNaturalValues_AndSpansLegacyAndNativeQuarters()
    {
        fixture.OutputHelper = output;
        var yearStart = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var q2Start = new DateTime(2025, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var q3Start = new DateTime(2025, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        var q4Start = new DateTime(2025, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var yearEnd = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var series = OctoObjectId.GenerateNewId();

        // Legacy quarterly totals: a time-range archive whose declared Period is the NATURAL 92 d
        // nominal quarter — the value an operator reads off the existing sbeg archive. A calendar
        // quarter accepts it because 92 d does not exceed the alignment's longest bucket. It is the
        // pre-cutover source of the quarterly rung DIRECTLY — no rung in between.
        var legacyQuarterly = await _builder.CreateTimeRangeArchiveAsync(
            "SbegLegacyQuarterly", TimeSpan.FromDays(92));
        await _builder.InsertWindowsAsync(legacyQuarterly, series,
        [
            (yearStart, q2Start, 100d),
            (q2Start, q3Start, 200d),
            (q3Start, q4Start, 300d),
        ]);

        // Native side: a base archive with the NATURAL 28 d monthly rung on top. A calendar month
        // nests inside a calendar quarter, so no synthetic bucket size is needed there either.
        var nativeBase = await CreateRawArchiveAsync("SbegNativeBase");
        await _builder.InsertPointsAsync(nativeBase, series,
        [
            (new DateTime(2025, 10, 5, 0, 0, 0, DateTimeKind.Utc), 1d),
            (new DateTime(2025, 11, 5, 0, 0, 0, DateTimeKind.Utc), 2d),
            (new DateTime(2025, 12, 5, 0, 0, 0, DateTimeKind.Utc), 3d),
        ]);
        var monthly = await _builder.CreateRollupAsync("SbegMonthly",
            [new RollupSourceReference(nativeBase)], TimeSpan.FromDays(28),
            bucketAlignment: BucketAlignment.CalendarMonth);
        await ActivateAsync(monthly);
        await _builder.RecomputeAsync(monthly, q4Start, yearEnd);

        // The quarterly rung activates with those natural values — 92 d legacy quarters and 28 d
        // months both nest inside a calendar quarter — and with ONE logical aggregation spec that
        // resolves verbatim on the legacy archive and through the child aggregation on the monthly
        // rung.
        var quarterly = await _builder.CreateRollupAsync("SbegQuarterly",
            [
                new RollupSourceReference(legacyQuarterly, ValidTo: q4Start),
                new RollupSourceReference(monthly, ValidFrom: q4Start),
            ],
            TimeSpan.FromDays(92), MultiSourceArchiveBuilder.LogicalSum, BucketAlignment.CalendarQuarter);
        await ActivateAsync(quarterly);
        (await LoadRollupAsync(quarterly)).Status.Should().Be(CkArchiveStatus.Activated,
            "the sbeg cutover shape activates with the sources' natural periods — nothing is re-declared");

        await _builder.RecomputeAsync(quarterly, yearStart, yearEnd);
        var quarters = await _builder.ReadBucketsAsync(quarterly);
        quarters.Select(b => b.WindowStart).Should().Equal([yearStart, q2Start, q3Start, q4Start]);
        quarters.Select(b => b.Value!.Value).Should().Equal([100d, 200d, 300d, 6d],
            "Q1–Q3 come from the legacy quarterly history, Q4 from the three fully contained months");

        // TC-E2E-04: the yearly rung is re-sourced from the quarterly rung and therefore inherits
        // the legacy history without knowing about it. Its single rollup source lets it keep the
        // pre-AB#5157 chained style — the physical column name, resolved verbatim.
        var yearly = await _builder.CreateRollupAsync("SbegYearly",
            [new RollupSourceReference(quarterly)], TimeSpan.FromDays(365),
            MultiSourceArchiveBuilder.CascadeSum, BucketAlignment.CalendarYear);
        await ActivateAsync(yearly);
        await _builder.RecomputeAsync(yearly, yearStart, yearEnd);

        var years = await _builder.ReadBucketsAsync(yearly);
        years.Should().ContainSingle();
        years[0].WindowStart.Should().Be(yearStart);
        years[0].Value!.Value.Should().BeApproximately(606d, Tolerance,
            "2025 = Q1 + Q2 + Q3 (legacy) + Q4 (native)");
    }

    // AB#5157 review: a calendar-aligned rung must be readable through the DOWNSAMPLING path (the
    // line chart), not only the direct table read. Calendar quarters are 90/91/92 days, so a
    // fixed-width DATE_BIN axis derived from the advisory 92 d bucket size drifts off the stored
    // windows and the §7 fully-contained predicate drops every one — the chart reads empty. Binning
    // on window_start keeps each calendar window as its own bin. UTC alignment keeps the expected
    // boundaries on clean quarter dates while still exercising the unequal-width geometry.
    [Fact]
    public async Task TC_E2E_10_CalendarQuarterRung_DownsamplesOntoItsCalendarWindows_NotEmpty()
    {
        fixture.OutputHelper = output;
        var yearStart = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var q2 = new DateTime(2025, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var q3 = new DateTime(2025, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        var q4 = new DateTime(2025, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var yearEnd = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var series = OctoObjectId.GenerateNewId();

        var baseArchive = await CreateRawArchiveAsync("CalQuarterBase");
        await _builder.InsertPointsAsync(baseArchive, series,
        [
            (yearStart.AddDays(10), 100d),
            (q2.AddDays(10), 200d),
            (q3.AddDays(10), 300d),
            (q4.AddDays(10), 400d),
        ]);
        var quarterly = await _builder.CreateRollupAsync("CalQuarterly",
            [new RollupSourceReference(baseArchive)], TimeSpan.FromDays(92),
            bucketAlignment: BucketAlignment.CalendarQuarter);
        await ActivateAsync(quarterly);
        await _builder.RecomputeAsync(quarterly, yearStart, yearEnd);

        // Sanity: the stored table holds the four calendar-quarter windows.
        var stored = await _builder.ReadBucketsAsync(quarterly, seriesRtId: series);
        stored.Select(b => b.WindowStart).Should().Equal([yearStart, q2, q3, q4]);
        stored.Select(b => b.Value!.Value).Should().Equal([100d, 200d, 300d, 400d]);

        // The chart path: downsampling the whole year with a pixel-sized target must return the four
        // quarter bins, not an empty result.
        var binned = await _builder.DownsampleAsync(quarterly, yearStart, yearEnd, targetPoints: 600, seriesRtId: series);
        var populated = binned.Where(b => b.Value is not null).ToList();
        populated.Select(b => b.Timestamp).Should().Equal([yearStart, q2, q3, q4],
            "each calendar quarter is its own bin, landing on the stored window_start");
        populated.Select(b => b.Value!.Value).Should().Equal([100d, 200d, 300d, 400d]);
    }

    // The zoned path #5 depends on: a calendar rung whose reference zone is Europe/Vienna. The bin
    // axis is computed in that zone, so it must agree with the stored (zone-derived) window_start
    // instants across a DST change. October 2025 is CEST (+2), November CET (+1) — the two month
    // starts sit at different UTC offsets. If the C#-side axis and the CrateDB rows disagreed by an
    // hour the populated bins would fall off the axis and read empty.
    [Fact]
    public async Task TC_E2E_11_CalendarMonthRung_InAZone_DownsamplesAcrossDst()
    {
        fixture.OutputHelper = output;
        const string vienna = "Europe/Vienna";
        var oct = new DateTime(2025, 9, 30, 22, 0, 0, DateTimeKind.Utc);  // 2025-10-01 00:00 Vienna (CEST)
        var nov = new DateTime(2025, 10, 31, 23, 0, 0, DateTimeKind.Utc); // 2025-11-01 00:00 Vienna (CET)
        var dec = new DateTime(2025, 11, 30, 23, 0, 0, DateTimeKind.Utc); // 2025-12-01 00:00 Vienna (CET)
        var series = OctoObjectId.GenerateNewId();

        var baseArchive = await CreateRawArchiveAsync("CalMonthDstBase");
        await _builder.InsertPointsAsync(baseArchive, series,
        [
            (oct.AddDays(5), 10d),
            (nov.AddDays(5), 20d),
        ]);
        var monthly = await _builder.CreateRollupAsync("CalMonthDst",
            [new RollupSourceReference(baseArchive)], TimeSpan.FromDays(28),
            bucketAlignment: BucketAlignment.CalendarMonth, referenceTimeZone: vienna);
        await ActivateAsync(monthly);
        await _builder.RecomputeAsync(monthly, oct, dec);

        var binned = await _builder.DownsampleAsync(monthly, oct, dec, targetPoints: 600, seriesRtId: series);
        var populated = binned.Where(b => b.Value is not null).ToList();
        populated.Select(b => b.Timestamp).Should().Equal([oct, nov],
            "the October (CEST) and November (CET) Vienna months are their own bins across the DST change");
        populated.Select(b => b.Value!.Value).Should().Equal([10d, 20d]);
    }

    // AB#5157 review, the other half of the same defect: the bin axis must be the ENGINE's business
    // for a fixed-size rung too. The line chart used to pre-align the window itself, because the
    // engine binned from whatever start it was handed and a window that does not sit on the grain
    // makes every bin straddle two stored hourly windows, so §7 drops them all and the chart reads
    // blank. A client cannot do that alignment correctly — neither the grain nor the rung's
    // alignment is part of the query contract — so the engine now snaps its own axis origin down to
    // the grain. A window starting mid-hour must therefore read exactly like the aligned one.
    [Fact]
    public async Task TC_E2E_12_FixedSizeRung_DownsampledFromAnUnalignedWindow_ReadsLikeTheAlignedOne()
    {
        fixture.OutputHelper = output;
        var day = new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Utc);
        var end = day.AddHours(6);
        var series = OctoObjectId.GenerateNewId();

        var baseArchive = await CreateRawArchiveAsync("UnalignedBase");
        await _builder.InsertPointsAsync(baseArchive, series,
            Enumerable.Range(0, 6).Select(h => (day.AddHours(h).AddMinutes(30), (double)(h + 1))));
        var hourly = await _builder.CreateRollupAsync("UnalignedHourly",
            [new RollupSourceReference(baseArchive)], TimeSpan.FromHours(1));
        await ActivateAsync(hourly);
        await _builder.RecomputeAsync(hourly, day, end);

        var aligned = await _builder.DownsampleAsync(hourly, day, end, targetPoints: 600, seriesRtId: series);
        var alignedPopulated = aligned.Where(b => b.Value is not null).ToList();
        alignedPopulated.Select(b => b.Timestamp).Should()
            .Equal(Enumerable.Range(0, 6).Select(h => day.AddHours(h)));
        alignedPopulated.Select(b => b.Value!.Value).Should().Equal([1d, 2d, 3d, 4d, 5d, 6d]);

        // The window a relative "last N hours" filter produces: an arbitrary sub-grain instant.
        var unaligned = await _builder.DownsampleAsync(
            hourly, day.AddMinutes(42).AddSeconds(54), end, targetPoints: 600, seriesRtId: series);
        var unalignedPopulated = unaligned.Where(b => b.Value is not null).ToList();

        unalignedPopulated.Select(b => b.Timestamp).Should()
            .Equal(alignedPopulated.Select(b => b.Timestamp),
                "the engine snaps the axis down to the grain, so the bins land on the stored windows");
        unalignedPopulated.Select(b => b.Value!.Value).Should()
            .Equal(alignedPopulated.Select(b => b.Value!.Value),
                "and each bin still covers a whole source window, including the one the request starts inside");
    }

    #endregion

    // ── aggregation helpers ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the AC1 ladder once per test class. Test classes in one collection run sequentially,
    /// so the gate only guards against a future parallelisation of the collection.
    /// </summary>
    private async Task<Ac1Ladder> Ac1Async()
    {
        await Ac1Gate.WaitAsync();
        try
        {
            return _ac1 ??= await BuildAc1LadderAsync();
        }
        finally
        {
            Ac1Gate.Release();
        }
    }

    private async Task<Ac1Ladder> BuildAc1LadderAsync()
    {
        var series = OctoObjectId.GenerateNewId();

        // ── legacy: a daily TIME-RANGE archive — one window per day, plus one window past the
        //    legacy source's ValidTo. It declares the CK path "Voltage" (a base archive never
        //    carries a rollup's physical column names), which is exactly what makes this the AC1
        //    shape: the rung's logical spec has to resolve differently on each source.
        var legacyWindows = await _builder.CreateTimeRangeArchiveAsync(
            "Ac1LegacyWindows", TimeSpan.FromDays(1));
        var legacyWindowRows = Enumerable.Range(0, 5)
            .Select(i => (Ac1FirstDay.AddDays(i), Ac1FirstDay.AddDays(i + 1), Ac1DailyValues[i]))
            .Append((Ac1PoisonLegacyDay, Ac1PoisonLegacyDay.AddDays(1), 999d));
        await _builder.InsertWindowsAsync(legacyWindows, series, legacyWindowRows);

        // ── native: hourly points for two days, plus two hours before the native ValidFrom ──
        var nativeBase = await CreateRawArchiveAsync("Ac1NativeBase");
        var nativePoints = Enumerable.Range(0, 48)
            .Select(i => (Ac1Cutover.AddHours(i), 1d))
            .Concat([(Ac1PoisonNativeDay.AddHours(10), 500d), (Ac1PoisonNativeDay.AddHours(11), 500d)]);
        await _builder.InsertPointsAsync(nativeBase, series, nativePoints);

        var hourly = await _builder.CreateRollupAsync("Ac1Hourly",
            [new RollupSourceReference(nativeBase)], OneHour);
        await ActivateAsync(hourly);
        await _builder.RecomputeAsync(hourly, Ac1PoisonNativeDay, Ac1NativeEnd);

        // ONE logical spec for both sources: verbatim on the time-range archive, through the child
        // aggregation on the hourly rung.
        var daily = await _builder.CreateRollupAsync("Ac1Daily",
            [
                new RollupSourceReference(legacyWindows, ValidTo: Ac1Cutover),
                new RollupSourceReference(hourly, ValidFrom: Ac1Cutover),
            ],
            TimeSpan.FromDays(1), MultiSourceArchiveBuilder.LogicalSum, BucketAlignment.CalendarDay);
        await ActivateAsync(daily);
        await _builder.RecomputeAsync(daily, Ac1FirstDay, Ac1PoisonLegacyDay.AddDays(1));

        var monthly = await _builder.CreateRollupAsync("Ac1Monthly",
            [new RollupSourceReference(daily)], TimeSpan.FromDays(28),
            MultiSourceArchiveBuilder.CascadeSum, BucketAlignment.CalendarMonth);
        await ActivateAsync(monthly);
        await _builder.RecomputeAsync(monthly,
            new DateTime(2025, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2025, 11, 1, 0, 0, 0, DateTimeKind.Utc));

        var yearly = await _builder.CreateRollupAsync("Ac1Yearly",
            [new RollupSourceReference(monthly)], TimeSpan.FromDays(365),
            MultiSourceArchiveBuilder.CascadeSum, BucketAlignment.CalendarYear);
        await ActivateAsync(yearly);
        await _builder.RecomputeAsync(yearly,
            new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        // ── control: ONE archive carrying the concatenated data at the rung's own grain ──
        var controlBase = await CreateRawArchiveAsync("Ac1ControlBase");
        await _builder.InsertPointsAsync(controlBase, series,
            Ac1Days.Select((day, i) => (day.AddHours(12), Ac1DailyValues[i])));
        var controlDaily = await _builder.CreateRollupAsync("Ac1ControlDaily",
            [new RollupSourceReference(controlBase)], TimeSpan.FromDays(1),
            bucketAlignment: BucketAlignment.CalendarDay);
        await ActivateAsync(controlDaily);
        await _builder.RecomputeAsync(controlDaily, Ac1FirstDay, Ac1PoisonLegacyDay.AddDays(1));

        return new Ac1Ladder(legacyWindows, nativeBase, hourly, daily, monthly, yearly,
            controlBase, controlDaily, series);
    }

    private static long ToEpochMs(DateTime value) => new DateTimeOffset(value, TimeSpan.Zero).ToUnixTimeMilliseconds();

    /// <summary>
    /// The compact cutover shape: two raw archives, one hourly point per bucket, a two-source 1 h
    /// rollup over <c>[H0, H0+6h)</c> split at <see cref="Cutover"/>, already aggregated forward
    /// (rewind + one tick). Returns the rollup and both sources.
    /// </summary>
    private async Task<(OctoObjectId Rollup, OctoObjectId Legacy, OctoObjectId Native)>
        BuildHourlyCutoverLadderAsync(string namePrefix, double legacyValue, double nativeValue)
    {
        var legacy = await CreateRawArchiveAsync($"{namePrefix}Legacy");
        var native = await CreateRawArchiveAsync($"{namePrefix}Native");
        var series = OctoObjectId.GenerateNewId();

        await _builder.InsertPointsAsync(legacy, series,
            Enumerable.Range(0, 3).Select(i => (H0.AddHours(i), legacyValue)));
        await _builder.InsertPointsAsync(native, series,
            Enumerable.Range(0, 3).Select(i => (Cutover.AddHours(i), nativeValue)));

        var rollupRtId = await CreateRollupAsync($"{namePrefix}Rollup",
        [
            new RollupSourceReference(legacy, ValidTo: Cutover),
            new RollupSourceReference(native, ValidFrom: Cutover),
        ]);
        await ActivateAsync(rollupRtId);
        await RewindAsync(rollupRtId, H0);
        await TickAsync(rollupRtId);

        return (rollupRtId, legacy, native);
    }

    /// <summary>
    /// Runs a stream-data query and returns every numeric cell value it produced, optionally
    /// filtered to the cells of interest (grouping queries also echo the group-by column).
    /// </summary>
    private async Task<IReadOnlyList<double>> QueryValuesAsync(
        string document, Func<JToken, bool>? cellFilter = null)
    {
        var result = await fixture.ExecuteGraphQlAsync(document, null, StreamDataFixture.StreamDataAdminPrincipal);
        result.Errors.Should().BeNullOrEmpty();

        var payload = JObject.Parse(fixture.SerializeGraphQl(result));
        var rows = payload.SelectTokens("$..rows.items[*]").ToList();
        var values = new List<double>();
        foreach (var cell in rows.SelectMany(r => r.SelectTokens("cells.items[*]")))
        {
            if (cellFilter is not null && !cellFilter(cell))
            {
                continue;
            }

            var raw = cell["value"];
            if (raw is null || raw.Type == JTokenType.Null)
            {
                continue;
            }

            // A windowed archive also echoes its window bounds as cells; only numeric cells are values.
            if (double.TryParse(raw.Value<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                values.Add(value);
            }
        }

        return values;
    }


    // ── reusable helpers ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// CK type id of the rollup archive entity, read back from the stored entity so the test never
    /// hard-codes the model-versioned name (the generic mutation addresses entities by it).
    /// </summary>
    private async Task<string> RollupArchiveCkTypeIdAsync(OctoObjectId rollupRtId)
    {
        var repository = fixture.GetSystemContext().GetSystemTenantRepository();
        using var session = await repository.GetSessionAsync();
        var entity = await repository.GetRtEntityByRtIdAsync<RtRollupArchive>(session, rollupRtId);
        entity.Should().NotBeNull();
        return entity!.CkTypeId!.ToString();
    }

    /// <summary>
    /// Creates and activates an isolated raw archive on the system tenant (default column
    /// <c>Voltage</c>) so a test never shares state with the fixture's canonical archive.
    /// </summary>
    private Task<OctoObjectId> CreateRawArchiveAsync(string namePrefix, params string[] columnPaths) =>
        _builder.CreateRawArchiveAsync(namePrefix, columnPaths);

    /// <summary>
    /// Writes one hourly <c>Voltage</c> sample per hour of the inclusive range [from, to] for a
    /// single series and forces read-after-write consistency on the archive table.
    /// </summary>
    private async Task InsertPointsAsync(OctoObjectId archiveRtId, DateTime from, DateTime to)
    {
        var points = new List<(DateTime, double)>();
        var voltage = 220.0;
        for (var timestamp = from; timestamp <= to; timestamp = timestamp.Add(OneHour))
        {
            points.Add((timestamp, voltage));
            voltage += 1.0;
        }

        await _builder.InsertPointsAsync(archiveRtId, OctoObjectId.GenerateNewId(), points);
    }

    /// <summary>
    /// Creates a rollup through the engine lifecycle service (bucket size 1 h, no watermark lag,
    /// SUM over <c>Voltage</c> unless overridden). Leaves it in <c>Created</c>.
    /// </summary>
    private Task<OctoObjectId> CreateRollupAsync(
        string namePrefix,
        IReadOnlyList<RollupSourceReference> sources,
        params CkRollupAggregationSpec[] aggregations) =>
        _builder.CreateRollupAsync(
            namePrefix, sources, OneHour,
            aggregations.Length == 0
                ? [new CkRollupAggregationSpec("Voltage", CkRollupFunction.Sum, null)]
                : aggregations);

    /// <summary>Drives an archive (raw or rollup) to Activated, provisioning its CrateDB table.</summary>
    private Task ActivateAsync(OctoObjectId archiveRtId) => _builder.ActivateAsync(archiveRtId);

    /// <summary>Runs one orchestrator pass for exactly this rollup and returns the committed buckets.</summary>
    private Task<int> TickAsync(OctoObjectId rollupRtId) => _builder.TickAsync(rollupRtId);

    /// <summary>
    /// Rewinds the rollup watermark so the next tick starts at a synthetic history instead of the
    /// activation-time "now" seed.
    /// </summary>
    private Task RewindAsync(OctoObjectId rollupRtId, DateTime toBucketEnd) =>
        _builder.RewindAsync(rollupRtId, toBucketEnd);

    /// <summary>CrateDB applies inserts asynchronously to the read path; force a refresh.</summary>
    private Task RefreshAsync(OctoObjectId archiveRtId) => _builder.RefreshAsync(archiveRtId);

    /// <summary>
    /// A <see cref="StreamDataController"/> bound to the fixture's real system context, with an
    /// HTTP context so the coverage endpoint's <c>RequestAborted</c> token resolves.
    /// </summary>
    private StreamDataController NewController() =>
        new(NullLogger<StreamDataController>.Instance, fixture.GetSystemContext(),
            A.Fake<IHostApplicationLifetime>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

    private static T OkValue<T>(ActionResult<T> response)
    {
        var ok = response.Result.Should().BeOfType<OkObjectResult>().Subject;
        return ok.Value.Should().BeAssignableTo<T>().Subject;
    }

    private Task<ITenantContext> TenantAsync() => _builder.TenantAsync();

    private async Task<RollupArchiveSnapshot> LoadRollupAsync(OctoObjectId rollupRtId)
    {
        var snapshot = await (await TenantAsync()).GetRollupArchiveRuntimeStore()!.GetAsync(rollupRtId);
        snapshot.Should().NotBeNull();
        return snapshot!;
    }

    /// <summary>Runs <c>rollupsFor(sourceRtId)</c> and returns the entry for the given rollup, or null.</summary>
    private async Task<JToken?> RollupsForAsync(OctoObjectId sourceRtId, OctoObjectId rollupRtId)
    {
        var result = await ExecuteAsync(@"
            query ($rtId: OctoObjectId!) {
              streamData {
                rollupsFor(rtId: $rtId) {
                  rtId status sourceArchiveRtId
                  sources { sourceArchiveRtId validFrom validTo }
                }
              }
            }", new { rtId = sourceRtId.ToString() });

        result.Errors.Should().BeNullOrEmpty();
        var rollups = (JArray)JObject.Parse(fixture.SerializeGraphQl(result))
            .SelectToken("data.streamData.rollupsFor")!;
        return rollups.FirstOrDefault(r => r["rtId"]!.Value<string>() == rollupRtId.ToString());
    }

    private Task<ExecutionResult> ExecuteAsync(string document, object variables) =>
        fixture.ExecuteGraphQlAsync(
            document, JsonSerializer.Serialize(variables), StreamDataFixture.StreamDataAdminPrincipal);
}
