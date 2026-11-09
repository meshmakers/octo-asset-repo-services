using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
/// AB#8647 — <c>groupByColumnPaths</c> on the transient <c>downsampling</c> query: one row per series (rtId) and gas day
/// over raw and rollup data, empty gas days of one series as Count 0, paging and totalCount over series x bins, the
/// ungrouped result unchanged, and the argument in the schema clients generate their types from.
/// <para>
/// Data: one value per hour for three series on the gas days (06:00 Europe/Vienna) starting 2026-03-27 (24 h),
/// 2026-03-28 (23 h, spring forward), 2026-03-29 (24 h), 2026-10-23 (24 h), 2026-10-24 (25 h, fall back) and 2026-10-25
/// (24 h). Series C has no value on the gas day starting 2026-03-29.
/// </para>
/// </summary>
[Collection(StreamDataMutatingCollection.Name)]
public class DownsamplingGroupByGraphQlTests(StreamDataFixture fixture, ITestOutputHelper output)
{
    private const string Zone = "Europe/Vienna";
    private const long GasDayMs = 21_600_000; // 06:00
    private const string CountCell = "voltage_count";

    private static readonly TimeZoneInfo Vienna = TimeZoneInfo.FindSystemTimeZoneById(Zone);

    private static readonly DateOnly[] SpringDays = [new(2026, 3, 27), new(2026, 3, 28), new(2026, 3, 29)];
    private static readonly DateOnly[] AutumnDays = [new(2026, 10, 23), new(2026, 10, 24), new(2026, 10, 25)];
    private static readonly DateOnly MissingDayOfC = new(2026, 3, 29);

    private readonly MultiSourceArchiveBuilder _builder = new(fixture);

    // ── AC1 + AC2: one row per rtId and gas day, 23 / 25 / 24 ─────────────────────────────────

    [Fact]
    public async Task GroupedByRtId_RawHourlyData_ReturnsOneRowPerSeriesAndGasDay_23_25_And24()
    {
        fixture.OutputHelper = output;
        var (raw, series) = await CreateRawWithThreeSeriesAsync("DsGroupRaw");

        foreach (var days in new[] { SpringDays, AutumnDays })
        {
            var rows = await GroupedRowsAsync(raw, GasDayStart(days[0]), GasDayStart(days[^1].AddDays(1)));

            rows.Should().HaveCount(days.Length * series.Length, "one row per rtId and gas day");
            rows.Select(r => (r.RtId, r.Bin)).Should().OnlyHaveUniqueItems();
            foreach (var rtId in series.Take(2))
            {
                rows.Where(r => r.RtId == rtId).Select(r => r.Bin).Should().Equal(days.Select(GasDayStart));
                foreach (var day in days)
                {
                    rows.Single(r => r.RtId == rtId && r.Bin == GasDayStart(day)).Count.Should().Be(ExpectedHours(day),
                        $"gas day {day:yyyy-MM-dd} of {rtId}");
                }
            }
        }
    }

    [Fact]
    public async Task GroupedByRtId_ReturnsTheRtIdAsCellAfterTheTimestamp_AndAsColumn()
    {
        fixture.OutputHelper = output;
        var (raw, series) = await CreateRawWithThreeSeriesAsync("DsGroupCells");

        var result = await ExecuteDownsamplingAsync(raw, GasDayStart(SpringDays[0]), GasDayStart(SpringDays[0].AddDays(1)),
            groupBy: ["rtId"], selection: "columns { attributePath } " + RowsSelection);

        var query = Downsampling(result);
        query.SelectTokens("columns[*].attributePath").Select(t => t.Value<string>())
            .Should().Equal("rtId", CountCell);
        var cellPaths = query.SelectTokens("rows.items[*]").Select(r =>
            string.Join(",", r.SelectTokens("cells.items[*].attributePath").Select(t => t.Value<string>()))).Distinct();
        cellPaths.Should().Equal($"timestamp,rtId,{CountCell}");
        ParseRows(query).Select(r => r.RtId).Should().BeEquivalentTo(series.Select(s => s.ToString()));
    }

    // ── AC3: empty gas day of one series is Count 0, on raw and on rollup ─────────────────────

    [Fact]
    public async Task GroupedByRtId_AGasDayWithoutValuesOfOneSeries_IsARowWithCount0_OnRawAndOnRollup()
    {
        fixture.OutputHelper = output;
        var (raw, series) = await CreateRawWithThreeSeriesAsync("DsGroupEmptyRaw");
        var rollup = await CreateGasDayRollupAsync(raw);
        await ActivateThroughGraphQlAsync(rollup);
        await BackfillThroughGraphQlAsync(rollup);

        var seriesC = series[2];
        foreach (var archive in new[] { raw, rollup })
        {
            var label = archive == raw ? "raw" : "rollup";
            var rows = await GroupedRowsAsync(archive, GasDayStart(SpringDays[0]), GasDayStart(SpringDays[^1].AddDays(1)),
                withSum: true);
            output.WriteLine($"{label}: {rows.Count} rows");

            rows.Should().HaveCount(SpringDays.Length * series.Length, $"every series gets every gas day ({label})");
            var empty = rows.Single(r => r.RtId == seriesC && r.Bin == GasDayStart(MissingDayOfC));
            empty.Count.Should().Be(0, $"a gas day without values of one series reads Count 0 ({label})");
            empty.Sum.Should().BeNull($"non-COUNT aggregations of an empty bin are null ({label})");

            rows.Single(r => r.RtId == seriesC && r.Bin == GasDayStart(new DateOnly(2026, 3, 28))).Count.Should().Be(23);
            rows.Single(r => r.RtId == series[0] && r.Bin == GasDayStart(MissingDayOfC)).Count.Should().Be(24);
        }
    }

    // ── AC4: paging and totalCount count rtId x bin ───────────────────────────────────────────

    [Fact]
    public async Task GroupedByRtId_PagingAndTotalCount_CountSeriesTimesBins()
    {
        fixture.OutputHelper = output;
        var (raw, series) = await CreateRawWithThreeSeriesAsync("DsGroupPaging");
        var from = GasDayStart(SpringDays[0]);
        var to = GasDayStart(SpringDays[^1].AddDays(1));
        var expected = SpringDays.Length * series.Length;

        var all = await GroupedRowsAsync(raw, from, to);
        all.Should().HaveCount(expected);

        var pages = new List<GroupedRow>();
        string? after = null;
        for (var page = 0; page < 10; page++)
        {
            var rowsArgs = after is null ? "first: 4" : $"first: 4, after: \"{after}\"";
            var query = Downsampling(await ExecuteDownsamplingAsync(raw, from, to, ["rtId"],
                "rows(" + rowsArgs + ") { totalCount pageInfo { hasNextPage endCursor } items { cells(first: 10) { items { attributePath value } } } }"));
            var rowsNode = query["rows"]!;
            rowsNode["totalCount"]!.Value<int>().Should().Be(expected, "totalCount counts rtId x bin rows");
            pages.AddRange(ParseRows(query));
            if (!rowsNode["pageInfo"]!["hasNextPage"]!.Value<bool>())
            {
                break;
            }

            after = rowsNode["pageInfo"]!["endCursor"]!.Value<string>();
        }

        pages.Should().HaveCount(expected);
        pages.Select(r => (r.RtId, r.Bin, r.Count)).Should().Equal(all.Select(r => (r.RtId, r.Bin, r.Count)),
            "the pages together are the unpaged result in the same order");
    }

    // ── fixed-width bins: grouped, but an empty bin stays one row without rtId ────────────────

    [Fact]
    public async Task GroupedByRtId_FixedWidthBins_AnEmptyBinIsOneRowWithANullRtId()
    {
        fixture.OutputHelper = output;
        var (raw, series) = await CreateRawWithThreeSeriesAsync("DsGroupFixedWidth");
        // Four 24 h bins from 2026-03-27T05:00Z; the data ends 2026-03-30T04:00Z, so the last bin has no samples.
        var from = new DateTime(2026, 3, 27, 5, 0, 0, DateTimeKind.Utc);
        var to = from.AddDays(4);
        var emptyBin = from.AddDays(3);

        var query = Downsampling(await ExecuteDownsamplingAsync(raw, from, to, ["rtId"], RowsSelection,
            fixedWidthLimit: 4));
        var rows = ParseRows(query);
        foreach (var row in rows)
        {
            output.WriteLine($"{row.Bin:O} {row.RtId} count={row.Count}");
        }

        var empty = rows.Where(r => r.Bin == emptyBin).ToList();
        empty.Should().ContainSingle("an empty fixed-width bin is not filled per series");
        empty[0].RtId.Should().BeNull();
        empty[0].Count.Should().Be(0);
        rows.Where(r => r.Bin == from).Select(r => r.RtId).Should().BeEquivalentTo(series,
            "a populated bin has one row per series");
        rows.Where(r => r.RtId is not null).Select(r => (r.RtId, r.Bin)).Should().OnlyHaveUniqueItems();
    }

    // ── AC5: without the argument the result is unchanged ─────────────────────────────────────

    [Fact]
    public async Task WithoutGroupBy_TheResultIsOneTotalRowPerBin_AsBefore()
    {
        fixture.OutputHelper = output;
        var (raw, _) = await CreateRawWithThreeSeriesAsync("DsGroupNone");

        var query = Downsampling(await ExecuteDownsamplingAsync(raw, GasDayStart(SpringDays[0]),
            GasDayStart(SpringDays[^1].AddDays(1)), groupBy: null,
            selection: "columns { attributePath } " + RowsSelection));

        query.SelectTokens("columns[*].attributePath").Select(t => t.Value<string>()).Should().Equal(CountCell);
        var rows = query.SelectTokens("rows.items[*]").ToList();
        rows.Should().HaveCount(SpringDays.Length, "one row per bin");
        foreach (var row in rows)
        {
            row.SelectTokens("cells.items[*].attributePath").Select(t => t.Value<string>())
                .Should().Equal("timestamp", CountCell);
        }

        var totals = rows.Select(r => r.SelectTokens("cells.items[*]")
            .First(c => c["attributePath"]!.Value<string>() == CountCell)["value"]!.Value<long>()).ToList();
        totals.Should().Equal(3 * 24L, 3 * 23L, 2 * 24L);
    }

    [Theory]
    [InlineData("Voltage")]
    [InlineData("timestamp")]
    public async Task GroupedByAnythingButRtId_IsRefused_NamingThePath(string path)
    {
        fixture.OutputHelper = output;
        var raw = await _builder.CreateRawArchiveAsync("DsGroupRefused");

        var result = await ExecuteDownsamplingAsync(raw, GasDayStart(SpringDays[0]), GasDayStart(SpringDays[1]),
            groupBy: [path], selection: RowsSelection, expectErrors: true);

        result.Errors.Should().NotBeNullOrEmpty();
        result.Errors!.Select(e => e.Message).Should().Contain(m => m.Contains($"'{path}'") && m.Contains("rtId"));
    }

    // ── AC6: the schema clients generate from carries the argument ────────────────────────────

    [Fact]
    public async Task Introspection_DownsamplingHasTheOptionalGroupByColumnPathsArgument()
    {
        fixture.OutputHelper = output;
        var result = await ExecuteAsync(@"
            query {
              __type(name: ""StreamDataTransient"") {
                fields { name args { name type { kind name ofType { kind name ofType { kind name } } } } }
              }
            }", new { });
        result.Errors.Should().BeNullOrEmpty();

        var fields = (JArray)Data(result).SelectToken("__type.fields")!;
        var downsampling = fields.Single(f => f["name"]!.Value<string>() == "downsampling");
        var arg = ((JArray)downsampling["args"]!).Single(a => a["name"]!.Value<string>() == "groupByColumnPaths");
        arg["type"]!["kind"]!.Value<string>().Should().Be("LIST", "optional: no NON_NULL wrapper");
        arg["type"]!["ofType"]!["kind"]!.Value<string>().Should().Be("NON_NULL");
        arg["type"]!["ofType"]!["ofType"]!["name"]!.Value<string>().Should().Be("String");

        // groupingAggregation keeps its required argument (additive change only).
        var grouping = fields.Single(f => f["name"]!.Value<string>() == "groupingAggregation");
        ((JArray)grouping["args"]!).Single(a => a["name"]!.Value<string>() == "groupByColumnPaths")["type"]!["kind"]!
            .Value<string>().Should().Be("NON_NULL");
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    private sealed record GroupedRow(string? RtId, DateTime Bin, long? Count, double? Sum);

    private const string RowsSelection =
        "rows(first: 100) { totalCount items { cells(first: 10) { items { attributePath value } } } }";

    private static DateTime GasDayStart(DateOnly day) =>
        TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(new TimeOnly(6, 0)), Vienna);

    private static long ExpectedHours(DateOnly day) =>
        (long)(GasDayStart(day.AddDays(1)) - GasDayStart(day)).TotalHours;

    private async Task<(OctoObjectId Raw, string[] Series)> CreateRawWithThreeSeriesAsync(string name)
    {
        var raw = await _builder.CreateRawArchiveAsync(name);
        var series = new[] { OctoObjectId.GenerateNewId(), OctoObjectId.GenerateNewId(), OctoObjectId.GenerateNewId() };
        for (var i = 0; i < series.Length; i++)
        {
            var points = new List<(DateTime, double)>();
            foreach (var day in SpringDays.Concat(AutumnDays))
            {
                if (i == 2 && day == MissingDayOfC)
                {
                    continue;
                }

                for (var t = GasDayStart(day); t < GasDayStart(day.AddDays(1)); t = t.AddHours(1))
                {
                    points.Add((t, 1d));
                }
            }

            await _builder.InsertPointsAsync(raw, series[i], points);
        }

        await _builder.RefreshAsync(raw);
        return (raw, series.Select(s => s.ToString()).ToArray());
    }

    private async Task<List<GroupedRow>> GroupedRowsAsync(OctoObjectId archive, DateTime from, DateTime to, bool withSum = false)
    {
        await _builder.RefreshAsync(archive);
        var result = await ExecuteDownsamplingAsync(archive, from, to, ["rtId"],
            "rows(first: 1000) { totalCount items { cells(first: 10) { items { attributePath value } } } }",
            withSum: withSum);
        var query = Downsampling(result);
        var rows = ParseRows(query);
        foreach (var row in rows)
        {
            output.WriteLine($"{row.Bin:O} {row.RtId} count={row.Count} sum={row.Sum}");
        }

        query["rows"]!["totalCount"]!.Value<int>().Should().Be(rows.Count);
        return rows;
    }

    private async Task<ExecutionResult> ExecuteDownsamplingAsync(OctoObjectId archive, DateTime from, DateTime to,
        string[]? groupBy, string selection, bool withSum = false, bool expectErrors = false, int? fixedWidthLimit = null)
    {
        var columns = withSum
            ? @"[{ attributePath: ""Voltage"", aggregationType: COUNT }, { attributePath: ""Voltage"", aggregationType: SUM }]"
            : @"[{ attributePath: ""Voltage"", aggregationType: COUNT }]";
        var groupByArg = groupBy is null ? "" : "groupByColumnPaths: $groupBy";
        var binArgs = fixedWidthLimit is { } limit
            ? $"limit: {limit}"
            : $@"timeZone: ""{Zone}"" binOrigin: ""06:00"" binInterval: 1";
        var groupByVar = groupBy is null ? "" : ", $groupBy: [String!]";
        var query = $@"
            query ($archiveRtId: OctoObjectId!, $from: DateTime!, $to: DateTime!{groupByVar}) {{
              streamData {{
                transientStreamDataQuery {{
                  downsampling(
                    archiveRtId: $archiveRtId
                    columnPaths: {columns}
                    from: $from
                    to: $to
                    {binArgs}
                    {groupByArg}
                    first: 1
                  ) {{
                    items {{ {selection} }}
                  }}
                }}
              }}
            }}";
        var variables = new Dictionary<string, object?>
        {
            ["archiveRtId"] = archive.ToString(),
            ["from"] = from.ToString("O"),
            ["to"] = to.ToString("O"),
        };
        if (groupBy is not null)
        {
            variables["groupBy"] = groupBy;
        }

        var result = await ExecuteAsync(query, variables);
        if (!expectErrors)
        {
            result.Errors.Should().BeNullOrEmpty();
        }

        return result;
    }

    private JToken Downsampling(ExecutionResult result) =>
        Data(result).SelectToken("streamData.transientStreamDataQuery.downsampling.items[0]")!;

    private static List<GroupedRow> ParseRows(JToken query)
    {
        var rows = new List<GroupedRow>();
        foreach (var row in query.SelectTokens("rows.items[*]"))
        {
            var cells = row.SelectTokens("cells.items[*]")
                .ToDictionary(c => c["attributePath"]!.Value<string>()!, c => c["value"]!);
            var count = cells.TryGetValue(CountCell, out var c) && c.Type != JTokenType.Null ? c.Value<long>() : (long?)null;
            var sum = cells.TryGetValue("voltage_sum", out var s) && s.Type != JTokenType.Null
                ? Convert.ToDouble(s.ToString(), CultureInfo.InvariantCulture)
                : (double?)null;
            var rtId = cells.TryGetValue("rtId", out var r) && r.Type != JTokenType.Null ? r.Value<string>() : null;
            rows.Add(new GroupedRow(rtId, ParseUtc(cells["timestamp"]), count, sum));
        }

        return rows;
    }

    private static DateTime ParseUtc(JToken timestamp) => timestamp.Type == JTokenType.Date
        ? AsUtc(timestamp.Value<DateTime>())
        : DateTime.Parse(timestamp.Value<string>()!, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value,
    };

    private async Task<OctoObjectId> CreateGasDayRollupAsync(OctoObjectId source)
    {
        var input = new Dictionary<string, object?>
        {
            ["rtWellKnownName"] = $"DsGroupGasDay{Guid.NewGuid():N}",
            ["sources"] = new[] { new Dictionary<string, object?> { ["sourceArchiveRtId"] = source.ToString() } },
            ["bucketSizeMs"] = (long)TimeSpan.FromDays(1).TotalMilliseconds,
            ["watermarkLagMs"] = 0L,
            ["bucketAlignment"] = "CALENDAR_DAY",
            ["referenceTimeZone"] = Zone,
            ["dayStartOffsetMs"] = GasDayMs,
            ["aggregations"] = new[]
            {
                new Dictionary<string, object?> { ["sourcePath"] = MultiSourceArchiveBuilder.VoltagePath, ["function"] = "COUNT" },
                new Dictionary<string, object?> { ["sourcePath"] = MultiSourceArchiveBuilder.VoltagePath, ["function"] = "SUM" },
            },
        };
        var result = await ExecuteAsync(@"
            mutation ($input: CreateRollupArchiveInput!) {
              streamData { createRollupArchive(input: $input) }
            }", new { input });
        result.Errors.Should().BeNullOrEmpty();
        return new OctoObjectId(Data(result).SelectToken("streamData.createRollupArchive")!.Value<string>()!);
    }

    private async Task ActivateThroughGraphQlAsync(OctoObjectId rtId)
    {
        var result = await ExecuteAsync(
            "mutation ($rtId: OctoObjectId!) { streamData { activateArchive(rtId: $rtId) { status } } }",
            new { rtId = rtId.ToString() });
        result.Errors.Should().BeNullOrEmpty();
    }

    private async Task BackfillThroughGraphQlAsync(OctoObjectId rollup)
    {
        var result = await ExecuteAsync(
            "mutation ($rtId: OctoObjectId!) { streamData { backfillRollupFromSource(rtId: $rtId) { rtId state } } }",
            new { rtId = rollup.ToString() });
        result.Errors.Should().BeNullOrEmpty();

        var orchestrator = (await _builder.TenantAsync()).GetRecomputeOrchestrator()!;
        for (var i = 0; i < 20; i++)
        {
            await orchestrator.TickAsync(CancellationToken.None);
            var snapshot = await _builder.LoadRollupAsync(rollup);
            if (!snapshot.RecomputeInProgress && snapshot.PendingRecomputeRanges == 0)
            {
                break;
            }
        }

        await _builder.RefreshAsync(rollup);
        await _builder.ExecuteSqlAsync($"REFRESH TABLE {_builder.GenMapTable(rollup)}");
    }

    private Task<ExecutionResult> ExecuteAsync(string query, object variables) =>
        fixture.ExecuteGraphQlAsync(query, JsonSerializer.Serialize(variables), StreamDataFixture.StreamDataAdminPrincipal);

    private JToken Data(ExecutionResult result) => JObject.Parse(fixture.SerializeGraphQl(result))["data"]!;
}
