using System.Text.Json;
using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Newtonsoft.Json.Linq;
using Xunit;
using Formatting = Newtonsoft.Json.Formatting;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
/// AB#5157 — the MEASURED-coverage filter of <c>resolveSeriesQuery</c>, driven through the GraphQL
/// surface against a real CrateDB ladder.
/// <para>
/// The filter runs ahead of the point/grain selection rules: a rung that holds no data for the
/// requested start is not a candidate, so a query reaching into history is answered from a coarser
/// rung that does cover it, with the truthful signal <c>COVERAGE_LIMITED</c>, the deliverable
/// <c>actualPoints</c>, a diagnostic naming the excluded finer rung and its available-from, and
/// <c>finerRungAvailableFrom</c> — the date from which the finer rung could serve.
/// </para>
/// <para>
/// Every assertion here depends on the host passing the tenant's coverage provider into the
/// resolver (<c>StreamDataQuery.ResolveSeriesQueryAsync</c>); a null provider keeps the filter inert
/// and every one of these tests would fall back to the pre-AB#5157 answer — which is exactly what
/// <see cref="NoRungReportsCoverage_KeepsThePreAb5157Answer"/> pins for the case where the filter is
/// legitimately inert.
/// </para>
/// </summary>
[Collection(StreamDataMutatingCollection.Name)]
public class ResolveSeriesQueryCoverageTests(StreamDataFixture fixture, ITestOutputHelper output)
{
    private readonly MultiSourceArchiveBuilder _builder = new(fixture);

    private static readonly TimeSpan OneHour = TimeSpan.FromHours(1);
    private static readonly TimeSpan SixHours = TimeSpan.FromHours(6);

    /// <summary>Anchor of the synthetic day the coverage ladder is built over.</summary>
    private static readonly DateTime W0 = new(2026, 7, 5, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Exclusive end of that day.</summary>
    private static readonly DateTime W1 = W0.AddHours(24);

    /// <summary>The finer rung was only aggregated from here on — the whole point of the ladder.</summary>
    private static readonly DateTime FineStart = W0.AddHours(18);

    private const long OneHourMs = 3_600_000L;
    private const long SixHoursMs = 21_600_000L;
    private const long TwelveHoursMs = 43_200_000L;

    /// <summary>
    /// The coverage ladder: one raw base holding the whole day, a 1 h rung aggregated only over the
    /// last six hours, and a 6 h rung aggregated over the whole day.
    /// </summary>
    private sealed record CoverageLadder(OctoObjectId Base, OctoObjectId Fine, OctoObjectId Coarse);

    private static readonly SemaphoreSlim LadderGate = new(1, 1);
    private static CoverageLadder? _ladder;

    // ── The coverage filter ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TC_RES_01_TheFinerRungWithoutCoverage_IsExcluded_AndTheAnswerIsCoverageLimited()
    {
        fixture.OutputHelper = output;
        var ladder = await LadderAsync();

        // Ideal bucket = 1 h, so the point/grain rule alone would pick the 1 h rung — but it holds
        // nothing before FineStart.
        var result = await ResolveAsync(ladder.Base, W0, W1, targetPoints: 24);

        result["archiveRtId"]!.Value<string>().Should().Be(ladder.Coarse.ToString(),
            "the coverage filter runs BEFORE the selection rules, so only the covering rungs compete");
        result["signal"]!.Value<string>().Should().Be("COVERAGE_LIMITED",
            "the filter changed the outcome — and CoverageLimited takes precedence over the "
            + "ResolutionLimited the covering rung alone would have produced (TC-RES-07)");
        result["effectiveBucketMs"]!.Value<long>().Should().Be(SixHoursMs);
        result["points"]!.Value<int>().Should().Be(4);
        result["actualPoints"]!.Value<int>().Should().Be(4,
            "actualPoints is what the selected rung really yields for the requested range (TC-RES-08)");
        result["finerRungAvailableFrom"]!.Value<DateTime>().Should().Be(FineStart,
            "the caller is told from when the finer rung could serve");

        var diagnostic = result["diagnostic"]!.Value<string>()!;
        diagnostic.Should().Contain(ladder.Fine.ToString(), "the diagnostic names the excluded rung");
        diagnostic.Should().Contain(FineStart.ToString("O"), "…and its available-from (TC-RES-09)");
    }

    [Fact]
    public async Task TC_RES_02_AvailableFromExactlyAtTheRequestedStart_CountsAsCovering()
    {
        fixture.OutputHelper = output;
        var ladder = await LadderAsync();

        var result = await ResolveAsync(ladder.Base, FineStart, W1, targetPoints: 6);

        result["archiveRtId"]!.Value<string>().Should().Be(ladder.Fine.ToString(),
            "coverage is at-or-before the requested start, not strictly before");
        result["signal"]!.Value<string>().Should().Be("OK");
        result["effectiveBucketMs"]!.Value<long>().Should().Be(OneHourMs);
        result["points"]!.Value<int>().Should().Be(6);
        result["finerRungAvailableFrom"]!.Type.Should().Be(JTokenType.Null,
            "nothing was excluded, so there is no finer rung to point at");
    }

    [Fact]
    public async Task TC_RES_03_ARungWhoseCoverageEndsBeforeTheRequestedEnd_StaysEligible()
    {
        fixture.OutputHelper = output;
        var ladder = await LadderAsync();

        // The window reaches a day past everything the ladder holds; only the START is filtered on.
        var result = await ResolveAsync(ladder.Base, FineStart, W1.AddDays(1), targetPoints: 30);

        result["archiveRtId"]!.Value<string>().Should().Be(ladder.Fine.ToString(),
            "the requested end plays no part in the coverage filter");
        result["signal"]!.Value<string>().Should().Be("OK");
    }

    [Fact]
    public async Task TC_RES_04_NoCoveringRung_FallsBackToTheRungsWithTheEarliestAvailableFrom()
    {
        fixture.OutputHelper = output;
        var ladder = await LadderAsync();

        // Twelve hours before ANY rung holds data. The base and the 6 h rung tie on the earliest
        // available-from (both start at W0) and stay candidates; the 1 h rung does not.
        var result = await ResolveAsync(ladder.Base, W0.AddHours(-12), W1, targetPoints: 36);

        result["archiveRtId"]!.Value<string>().Should().Be(ladder.Coarse.ToString(),
            "the selection rules pick among the tied earliest rungs — no error is returned");
        result["signal"]!.Value<string>().Should().Be("COVERAGE_LIMITED");
        result["actualPoints"]!.Value<int>().Should().Be(6, "36 h over 6 h buckets");
        result["diagnostic"]!.Value<string>().Should().Contain(ladder.Fine.ToString());
    }

    [Fact]
    public async Task TC_RES_06_AFilterThatDoesNotChangeTheOutcome_KeepsTheOkAnswer()
    {
        fixture.OutputHelper = output;
        var ladder = await LadderAsync();

        // Ideal bucket = 6 h: the coarsest fine-enough rung wins with or without the filter.
        var result = await ResolveAsync(ladder.Base, W0, W1, targetPoints: 4);

        result["archiveRtId"]!.Value<string>().Should().Be(ladder.Coarse.ToString());
        result["signal"]!.Value<string>().Should().Be("OK",
            "CoverageLimited is reserved for a filter that actually redirected the query");
        result["actualPoints"]!.Type.Should().Be(JTokenType.Null, "the target was met");
        result["finerRungAvailableFrom"]!.Type.Should().Be(JTokenType.Null);
        result["diagnostic"]!.Type.Should().Be(JTokenType.Null);
    }

    [Fact]
    public async Task NoRungReportsCoverage_KeepsThePreAb5157Answer()
    {
        fixture.OutputHelper = output;
        var baseRtId = await _builder.CreateRawArchiveAsync("ResInertBase");
        var fine = await _builder.CreateRollupAsync(
            "ResInertFine", [new RollupSourceReference(baseRtId)], OneHour);
        await _builder.ActivateAsync(fine);
        var coarse = await _builder.CreateRollupAsync(
            "ResInertCoarse", [new RollupSourceReference(baseRtId)], SixHours);
        await _builder.ActivateAsync(coarse);

        // Every table exists and every one of them is empty, so no rung reports coverage.
        var result = await ResolveAsync(baseRtId, W0, W1, targetPoints: 24);

        result["archiveRtId"]!.Value<string>().Should().Be(fine.ToString(),
            "with no measured coverage anywhere the filter is inert and the pre-AB#5157 point/grain "
            + "rule decides alone — the finest rung wins even though it holds nothing");
        result["archiveRtId"]!.Value<string>().Should().NotBe(coarse.ToString(),
            "no rung was excluded, so nothing pushed the answer onto the coarser rung");
        result["signal"]!.Value<string>().Should().Be("OK");
        result["points"]!.Value<int>().Should().Be(24);
        result["finerRungAvailableFrom"]!.Type.Should().Be(JTokenType.Null);
    }

    // ── TC-E2E-08: coverage and resolution over the AC1 cutover ladder ───────────────────────
    //
    // legacy 6 h TIME-RANGE archive ──[validTo cutover)──┐
    //                                                     ├──> 12 h rung D
    // native base ──> hourly rung H  ──[validFrom cutover)┘
    //
    // AC1 verbatim: the legacy history is the TIME-RANGE archive itself and the native history a
    // rollup, both sources of one rung under a single LOGICAL aggregation spec ("Voltage", Sum) —
    // resolved per source since AB#5157 (the declared CK path here, the hourly rung's own
    // 'voltage_sum' there). Coarse legacy history before the cutover, fine native after it.

    [Fact]
    public async Task TC_E2E_08_OverTheCutoverLadder_EveryRungReportsItsOwnMeasuredCoverage()
    {
        fixture.OutputHelper = output;
        var ladder = await CutoverLadderAsync();

        var coverage = await ExecuteAsync(@"
            query ($rtId: OctoObjectId!) {
              streamData {
                coverageFor(rtId: $rtId) {
                  archiveRtId bucketSizeMs availableFrom availableTo
                }
              }
            }", new { rtId = ladder.NativeBase.ToString() });

        var rungs = (JArray)JObject.Parse(fixture.SerializeGraphQl(coverage))
            .SelectToken("data.streamData.coverageFor")!;
        rungs.Select(r => r["archiveRtId"]!.Value<string>()).Should().Equal(
            new[] { ladder.NativeBase.ToString(), ladder.Hourly.ToString(), ladder.Daily.ToString() },
            "the queried base first, then its transitive dependents breadth-first");

        rungs[1]["availableFrom"]!.Value<DateTime>().Should().Be(ladder.Cutover,
            "the hourly rung only exists from the cutover");
        rungs[1]["availableTo"]!.Value<DateTime>().Should().Be(ladder.End);
        rungs[2]["availableFrom"]!.Value<DateTime>().Should().Be(ladder.Anchor,
            "the multi-source rung reaches back to the start of the legacy history — before its own "
            + "base archive holds anything");
        rungs[2]["availableTo"]!.Value<DateTime>().Should().Be(ladder.End);
        rungs[2]["bucketSizeMs"]!.Value<long>().Should().Be(TwelveHoursMs);
    }

    [Fact]
    public async Task TC_E2E_08_OverTheCutoverLadder_ResolutionPicksTheRungThatCoversTheRequestedStart()
    {
        fixture.OutputHelper = output;
        var ladder = await CutoverLadderAsync();

        // resolveSeriesQuery over a range starting in the legacy era: only the 12 h rung holds
        // anything there, so it must be the answer even though it is coarser than requested.
        var result = await ResolveAsync(ladder.NativeBase, ladder.Anchor, ladder.End, targetPoints: 24);
        var resolved = result.ToString(Formatting.None);

        result["archiveRtId"]!.Value<string>().Should().Be(ladder.Daily.ToString(),
            "only the multi-source rung covers the requested start; resolveSeriesQuery answered {0}",
            resolved);
        result["signal"]!.Value<string>().Should().Be("COVERAGE_LIMITED");
        result["effectiveBucketMs"]!.Value<long>().Should().Be(TwelveHoursMs);
        result["actualPoints"]!.Value<int>().Should().Be(2,
            "24 h of history at the 12 h grain of the covering rung");
        result["finerRungAvailableFrom"]!.Value<DateTime>().Should().Be(ladder.Cutover);
        result["diagnostic"]!.Value<string>().Should()
            .Contain(ladder.Hourly.ToString(), "the excluded rung is the hourly one")
            .And.Contain(ladder.Cutover.ToString("O"), "…named together with its available-from");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The archives of the AC1 cutover ladder, built once for the whole class.</summary>
    private sealed record CutoverLadder(
        OctoObjectId LegacyWindows,
        OctoObjectId NativeBase,
        OctoObjectId Hourly,
        OctoObjectId Daily,
        DateTime Anchor,
        DateTime Cutover,
        DateTime End);

    private static readonly SemaphoreSlim CutoverGate = new(1, 1);
    private static CutoverLadder? _cutoverLadder;

    private async Task<CutoverLadder> CutoverLadderAsync()
    {
        await CutoverGate.WaitAsync();
        try
        {
            return _cutoverLadder ??= await BuildCutoverLadderAsync();
        }
        finally
        {
            CutoverGate.Release();
        }
    }

    private async Task<CutoverLadder> BuildCutoverLadderAsync()
    {
        var anchor = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var cutover = anchor.AddHours(12);
        var end = anchor.AddHours(24);
        var series = OctoObjectId.GenerateNewId();

        var legacyWindows = await _builder.CreateTimeRangeArchiveAsync("E2E08LegacyWindows", SixHours);
        await _builder.InsertWindowsAsync(legacyWindows, series,
            Enumerable.Range(0, 2).Select(i =>
                (anchor.Add(SixHours * i), anchor.Add(SixHours * (i + 1)), 60d)));

        var nativeBase = await _builder.CreateRawArchiveAsync("E2E08NativeBase");
        await _builder.InsertPointsAsync(nativeBase, series,
            Enumerable.Range(0, 12).Select(i => (cutover.AddHours(i), 1d)));
        var hourly = await _builder.CreateRollupAsync(
            "E2E08Hourly", [new RollupSourceReference(nativeBase)], OneHour);
        await _builder.ActivateAsync(hourly);
        await _builder.RecomputeAsync(hourly, cutover, end);

        var daily = await _builder.CreateRollupAsync("E2E08Daily",
            [
                new RollupSourceReference(legacyWindows, ValidTo: cutover),
                new RollupSourceReference(hourly, ValidFrom: cutover),
            ],
            TimeSpan.FromHours(12), MultiSourceArchiveBuilder.LogicalSum);
        await _builder.ActivateAsync(daily);
        await _builder.RecomputeAsync(daily, anchor, end);

        return new CutoverLadder(legacyWindows, nativeBase, hourly, daily, anchor, cutover, end);
    }

    /// <summary>
    /// Builds the coverage ladder once for the whole class: a raw base holding one hourly point per
    /// hour of <c>[W0, W1)</c>, a 1 h rung backfilled only over <c>[FineStart, W1)</c> and a 6 h rung
    /// backfilled over the whole day.
    /// </summary>
    private async Task<CoverageLadder> LadderAsync()
    {
        await LadderGate.WaitAsync();
        try
        {
            return _ladder ??= await BuildLadderAsync();
        }
        finally
        {
            LadderGate.Release();
        }
    }

    private async Task<CoverageLadder> BuildLadderAsync()
    {
        var baseRtId = await _builder.CreateRawArchiveAsync("ResBase");
        await _builder.InsertPointsAsync(baseRtId, OctoObjectId.GenerateNewId(),
            Enumerable.Range(0, 24).Select(i => (W0.AddHours(i), 1d)));

        var fine = await _builder.CreateRollupAsync(
            "ResFine", [new RollupSourceReference(baseRtId)], OneHour);
        await _builder.ActivateAsync(fine);
        await _builder.RecomputeAsync(fine, FineStart, W1);

        var coarse = await _builder.CreateRollupAsync(
            "ResCoarse", [new RollupSourceReference(baseRtId)], SixHours);
        await _builder.ActivateAsync(coarse);
        await _builder.RecomputeAsync(coarse, W0, W1);

        return new CoverageLadder(baseRtId, fine, coarse);
    }

    /// <summary>
    /// Runs <c>resolveSeriesQuery</c> for a SUM series over the <c>Voltage</c> path and returns the
    /// result node.
    /// </summary>
    private async Task<JToken> ResolveAsync(
        OctoObjectId baseArchiveRtId, DateTime from, DateTime to, int targetPoints)
    {
        var result = await ExecuteAsync(@"
            query ($input: ResolveSeriesQueryInput!) {
              streamData {
                resolveSeriesQuery(input: $input) {
                  archiveRtId effectiveBucketMs points reducingFunction signal
                  actualPoints diagnostic finerRungAvailableFrom
                }
              }
            }",
            new
            {
                input = new
                {
                    baseArchiveRtId = baseArchiveRtId.ToString(),
                    from = from.ToString("O"),
                    to = to.ToString("O"),
                    targetPoints,
                    requiredAggregation = "SUM",
                    sourcePath = MultiSourceArchiveBuilder.VoltagePath,
                },
            });

        var node = JObject.Parse(fixture.SerializeGraphQl(result))
            .SelectToken("data.streamData.resolveSeriesQuery");
        node.Should().NotBeNull("stream data is enabled, so the resolver answers");
        node!["reducingFunction"]!.Value<string>().Should().Be("SUM",
            "the caller-supplied aggregation is never re-guessed");
        return node;
    }

    private async Task<ExecutionResult> ExecuteAsync(string document, object variables)
    {
        var result = await fixture.ExecuteGraphQlAsync(
            document, JsonSerializer.Serialize(variables), StreamDataFixture.StreamDataAdminPrincipal);
        result.Errors.Should().BeNullOrEmpty();
        return result;
    }
}
