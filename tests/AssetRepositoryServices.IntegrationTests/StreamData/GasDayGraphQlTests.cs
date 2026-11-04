using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
/// AB#7361 — gas days end to end through the GraphQL API: a Count rollup with a day start of 06:00 in
/// <c>Europe/Vienna</c> created by <c>createRollupArchive</c>, a seeded one, the local-day bins of the transient
/// <c>downsampling</c> query over the raw archive and over the rollup, and <c>resolveSeriesQuery</c> choosing the
/// gas-day rollup for a gas-day request.
/// <para>
/// Data: one value per hour (complete hourly data) for the gas days starting 2026-03-28 (23 h, spring forward),
/// 2026-07-01 (24 h) and 2026-10-24 (25 h, fall back); the gas day starting 2026-07-02 holds no data.
/// </para>
/// </summary>
[Collection(StreamDataMutatingCollection.Name)]
public class GasDayGraphQlTests(StreamDataFixture fixture, ITestOutputHelper output)
{
    private const long GasDayMs = 21_600_000; // 06:00
    private const string Zone = "Europe/Vienna";
    private const string CountColumn = "voltage_count";

    // Gas-day starts in UTC (06:00 local).
    private static readonly DateTime SpringGasDay = new(2026, 3, 28, 5, 0, 0, DateTimeKind.Utc);  // 23 h
    private static readonly DateTime SummerGasDay = new(2026, 7, 1, 4, 0, 0, DateTimeKind.Utc);   // 24 h
    private static readonly DateTime EmptyGasDay = new(2026, 7, 2, 4, 0, 0, DateTimeKind.Utc);    // no data
    private static readonly DateTime AutumnGasDay = new(2026, 10, 24, 4, 0, 0, DateTimeKind.Utc); // 25 h

    private static readonly (DateTime Start, DateTime End)[] FilledGasDays =
    [
        (SpringGasDay, new DateTime(2026, 3, 29, 4, 0, 0, DateTimeKind.Utc)),
        (SummerGasDay, new DateTime(2026, 7, 2, 4, 0, 0, DateTimeKind.Utc)),
        (AutumnGasDay, new DateTime(2026, 10, 25, 5, 0, 0, DateTimeKind.Utc)),
    ];

    private readonly MultiSourceArchiveBuilder _builder = new(fixture);

    // ── AC1: create, validation, rollupsFor ───────────────────────────────────────────────────

    [Fact]
    public async Task CreateRollupArchive_StoresTheDayStart_AndRollupsForReturnsIt()
    {
        fixture.OutputHelper = output;
        var raw = await _builder.CreateRawArchiveAsync("GasDayCreateRaw");

        var rollup = await CreateGasDayRollupAsync(raw, GasDayMs);

        var snapshot = await _builder.LoadRollupAsync(rollup);
        snapshot.DayStartOffsetMs.Should().Be(GasDayMs);
        snapshot.BucketAlignment.Should().Be(BucketAlignment.CalendarDay);
        snapshot.ReferenceTimeZone.Should().Be(Zone);

        var entry = await RollupsForAsync(raw, rollup);
        entry["referenceTimeZone"]!.Value<string>().Should().Be(Zone);
        entry["dayStartOffsetMs"]!.Value<long>().Should().Be(GasDayMs);
        entry["bucketAlignment"]!.Value<string>().Should().Be("CalendarDay");
    }

    [Fact]
    public async Task CreateRollupArchive_WithoutDayStart_IsAMidnightRollup()
    {
        fixture.OutputHelper = output;
        var raw = await _builder.CreateRawArchiveAsync("GasDayMidnightRaw");

        var rollup = await CreateGasDayRollupAsync(raw, dayStartOffsetMs: null);

        (await _builder.LoadRollupAsync(rollup)).DayStartOffsetMs.Should().BeNull();
        (await RollupsForAsync(raw, rollup))["dayStartOffsetMs"]!.Type.Should().Be(JTokenType.Null);
    }

    [Theory]
    [InlineData(86_400_000L, "CALENDAR_DAY", Zone, "DayStartOffsetMs")]
    [InlineData(-60_000L, "CALENDAR_DAY", Zone, "DayStartOffsetMs")]
    [InlineData(21_600_001L, "CALENDAR_DAY", Zone, "DayStartOffsetMs")]
    [InlineData(21_600_000L, "CALENDAR_MONTH", Zone, "DayStartOffsetMs")]
    [InlineData(21_600_000L, "CALENDAR_DAY", "Europe/Atlantis", "ReferenceTimeZone")]
    public async Task CreateRollupArchive_WithAnInvalidDayStart_IsRefused_NamingTheField(
        long dayStartOffsetMs, string alignment, string zone, string namedField)
    {
        fixture.OutputHelper = output;
        var raw = await _builder.CreateRawArchiveAsync("GasDayInvalidRaw");

        var result = await ExecuteAsync(CreateMutation, new
        {
            input = RollupInput(raw, dayStartOffsetMs, alignment, zone),
        });

        result.Errors.Should().NotBeNullOrEmpty();
        var error = result.Errors!.FirstOrDefault(e => e.InnerException is not null) ?? result.Errors!.First();
        output.WriteLine($"{error.Code}: {error.Message}");
        error.Code.Should().NotBeNullOrEmpty("the refusal is a structured error");
        error.Message.Should().ContainEquivalentOf(namedField);
    }

    // ── AC2: a seeded rollup (ImportRt / blueprint seed writes the entity directly) ────────────

    [Fact]
    public async Task SeededRollupWithDayStart_Activates_AndAggregatesGasDays()
    {
        fixture.OutputHelper = output;
        var raw = await _builder.CreateRawArchiveAsync("GasDaySeedRaw");
        var series = OctoObjectId.GenerateNewId();
        await InsertHourlyAsync(raw, series);

        // A midnight rollup created through the engine, then rewritten onto the entity the way an ImportRt seed writes
        // it: the day start reaches the rollup without the lifecycle service.
        var rollup = await _builder.CreateRollupAsync(
            "GasDaySeeded", [new RollupSourceReference(raw)], TimeSpan.FromDays(1),
            [new CkRollupAggregationSpec(MultiSourceArchiveBuilder.VoltagePath, CkRollupFunction.Count, null)],
            BucketAlignment.CalendarDay, Zone);
        await SeedDayStartAsync(rollup, GasDayMs);
        (await _builder.LoadRollupAsync(rollup)).DayStartOffsetMs.Should().Be(GasDayMs);

        await _builder.ActivateAsync(rollup);
        await _builder.RecomputeAsync(rollup, SpringGasDay, FilledGasDays[^1].End);

        var buckets = await _builder.ReadBucketsAsync(rollup, CountColumn, series);
        buckets.Should().Contain((SpringGasDay, 23d));
        buckets.Should().Contain((SummerGasDay, 24d));
        buckets.Should().Contain((AutumnGasDay, 25d));
        buckets.Select(b => b.WindowStart).Should().OnlyContain(
            start => IsLocalSixOClock(start), "every bucket of a seeded gas-day rollup starts at 06:00 local time");
    }

    // ── AC3: end to end over the rollup and over the raw archive ──────────────────────────────

    [Fact]
    public async Task GasDayCounts_FromTheRollupAndFromTheRawArchive_Are23_25_24_And0()
    {
        fixture.OutputHelper = output;
        var raw = await _builder.CreateRawArchiveAsync("GasDayE2ERaw");
        var series = OctoObjectId.GenerateNewId();
        await InsertHourlyAsync(raw, series);

        var rollup = await CreateGasDayRollupAsync(raw, GasDayMs);
        await ActivateThroughGraphQlAsync(rollup);
        await BackfillThroughGraphQlAsync(rollup);

        var from = SpringGasDay;
        var to = FilledGasDays[^1].End;

        foreach (var archive in new[] { rollup, raw })
        {
            var counts = await GasDayCountsAsync(archive, from, to);
            output.WriteLine($"{(archive == rollup ? "rollup" : "raw")}: {counts.Count} bins");

            counts[SpringGasDay].Should().Be(23, "the gas day with the spring-forward change has 23 hours");
            counts[AutumnGasDay].Should().Be(25, "the gas day with the fall-back change has 25 hours");
            counts[SummerGasDay].Should().Be(24);
            counts[EmptyGasDay].Should().Be(0, "a gas day without data is an empty bin with Count 0");
        }
    }

    // ── rollup selection: a gas-day request reads the gas-day rollup ──────────────────────────

    [Fact]
    public async Task ResolveSeriesQuery_ForGasDays_ChoosesTheGasDayRollup_AndAMidnightRequestDoesNot()
    {
        fixture.OutputHelper = output;
        var raw = await _builder.CreateRawArchiveAsync("GasDayResolveRaw");
        var series = OctoObjectId.GenerateNewId();
        await InsertHourlyAsync(raw, series);

        var gasDay = await CreateGasDayRollupAsync(raw, GasDayMs);
        var midnight = await CreateGasDayRollupAsync(raw, dayStartOffsetMs: null);
        foreach (var rollup in new[] { gasDay, midnight })
        {
            await ActivateThroughGraphQlAsync(rollup);
            await BackfillThroughGraphQlAsync(rollup);
        }

        var from = SummerGasDay;
        var to = SummerGasDay.AddDays(1);

        var byOrigin = await ResolveAsync(raw, from, to, new { binOrigin = "06:00" });
        byOrigin["archiveRtId"]!.Value<string>().Should().Be(gasDay.ToString(),
            "a gas-day request is answered from the gas-day rollup");

        var byMs = await ResolveAsync(raw, from, to, new { dayStartOffsetMs = GasDayMs });
        byMs["archiveRtId"]!.Value<string>().Should().Be(gasDay.ToString());

        var midnightRequest = await ResolveAsync(raw, from.AddHours(-4), to.AddHours(-4), new { });
        midnightRequest["archiveRtId"]!.Value<string>().Should().NotBe(gasDay.ToString(),
            "a midnight request never reads gas days");
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    private const string CreateMutation = @"
        mutation ($input: CreateRollupArchiveInput!) {
          streamData { createRollupArchive(input: $input) }
        }";

    private static Dictionary<string, object?> RollupInput(
        OctoObjectId source, long? dayStartOffsetMs, string alignment = "CALENDAR_DAY", string zone = Zone)
    {
        var input = new Dictionary<string, object?>
        {
            ["rtWellKnownName"] = $"GasDay{Guid.NewGuid():N}",
            ["sources"] = new[] { new Dictionary<string, object?> { ["sourceArchiveRtId"] = source.ToString() } },
            ["bucketSizeMs"] = (long)TimeSpan.FromDays(1).TotalMilliseconds,
            ["watermarkLagMs"] = 0L,
            ["bucketAlignment"] = alignment,
            ["referenceTimeZone"] = zone,
            ["aggregations"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["sourcePath"] = MultiSourceArchiveBuilder.VoltagePath,
                    ["function"] = "COUNT",
                },
            },
        };
        if (dayStartOffsetMs is { } ms)
        {
            input["dayStartOffsetMs"] = ms;
        }

        return input;
    }

    private async Task<OctoObjectId> CreateGasDayRollupAsync(OctoObjectId source, long? dayStartOffsetMs)
    {
        var result = await ExecuteAsync(CreateMutation, new { input = RollupInput(source, dayStartOffsetMs) });
        result.Errors.Should().BeNullOrEmpty();
        var rtId = Data(result).SelectToken("streamData.createRollupArchive")!.Value<string>();
        return new OctoObjectId(rtId!);
    }

    private async Task ActivateThroughGraphQlAsync(OctoObjectId rtId)
    {
        var result = await ExecuteAsync(
            "mutation ($rtId: OctoObjectId!) { streamData { activateArchive(rtId: $rtId) { status } } }",
            new { rtId = rtId.ToString() });
        result.Errors.Should().BeNullOrEmpty();
    }

    /// <summary>
    /// <c>backfillRollupFromSource</c> only enqueues the job; the background orchestrator runs it. The test drains it
    /// by ticking the recompute orchestrator until the job is no longer pending or running.
    /// </summary>
    private async Task BackfillThroughGraphQlAsync(OctoObjectId rollup)
    {
        var result = await ExecuteAsync(
            "mutation ($rtId: OctoObjectId!) { streamData { backfillRollupFromSource(rtId: $rtId) { rtId state } } }",
            new { rtId = rollup.ToString() });
        result.Errors.Should().BeNullOrEmpty();
        Data(result).SelectToken("streamData.backfillRollupFromSource").Should().NotBeNull("the source holds data");

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

    private async Task<Dictionary<DateTime, double>> GasDayCountsAsync(OctoObjectId archive, DateTime from, DateTime to)
    {
        await _builder.RefreshAsync(archive);
        var result = await ExecuteAsync(@"
            query ($archiveRtId: OctoObjectId!, $from: DateTime!, $to: DateTime!) {
              streamData {
                transientStreamDataQuery {
                  downsampling(
                    archiveRtId: $archiveRtId
                    columnPaths: [{ attributePath: ""Voltage"", aggregationType: COUNT }]
                    from: $from
                    to: $to
                    timeZone: ""Europe/Vienna""
                    binOrigin: ""06:00""
                    binInterval: 1
                    first: 1
                  ) {
                    items { rows(first: 1000) { items { cells(first: 5) { items { attributePath value } } } } }
                  }
                }
              }
            }",
            new { archiveRtId = archive.ToString(), from = from.ToString("O"), to = to.ToString("O") });
        result.Errors.Should().BeNullOrEmpty();

        var counts = new Dictionary<DateTime, double>();
        var rows = Data(result).SelectTokens("streamData.transientStreamDataQuery.downsampling.items[*].rows.items[*]");
        foreach (var row in rows)
        {
            var cells = row.SelectTokens("cells.items[*]").ToList();
            var timestamp = cells.First(c => c["attributePath"]!.Value<string>() == "timestamp")["value"]!;
            var count = cells.First(c => c["attributePath"]!.Value<string>() != "timestamp")["value"]!;
            var start = timestamp.Type == JTokenType.Date
                ? AsUtc(timestamp.Value<DateTime>())
                : DateTime.Parse(timestamp.Value<string>()!, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            counts[start] = count.Type == JTokenType.Null ? double.NaN : count.Value<double>();
        }

        return counts;
    }

    private async Task<JToken> ResolveAsync(OctoObjectId baseArchive, DateTime from, DateTime to, object dayStart)
    {
        var input = new Dictionary<string, object?>
        {
            ["baseArchiveRtId"] = baseArchive.ToString(),
            ["from"] = from.ToString("O"),
            ["to"] = to.ToString("O"),
            ["targetPoints"] = 1,
            ["requiredAggregation"] = "COUNT",
            ["sourcePath"] = MultiSourceArchiveBuilder.VoltagePath,
            ["timeZone"] = Zone,
        };
        foreach (var property in dayStart.GetType().GetProperties())
        {
            input[property.Name] = property.GetValue(dayStart);
        }

        var result = await ExecuteAsync(@"
            query ($input: ResolveSeriesQueryInput!) {
              streamData { resolveSeriesQuery(input: $input) { archiveRtId effectiveBucketMs signal diagnostic } }
            }", new { input });
        result.Errors.Should().BeNullOrEmpty();
        var node = Data(result).SelectToken("streamData.resolveSeriesQuery")!;
        output.WriteLine(node.ToString());
        return node;
    }

    private async Task<JToken> RollupsForAsync(OctoObjectId source, OctoObjectId rollup)
    {
        var result = await ExecuteAsync(@"
            query ($rtId: OctoObjectId!) {
              streamData { rollupsFor(rtId: $rtId) { rtId bucketAlignment referenceTimeZone dayStartOffsetMs } }
            }", new { rtId = source.ToString() });
        result.Errors.Should().BeNullOrEmpty();
        return ((JArray)Data(result).SelectToken("streamData.rollupsFor")!)
            .Single(r => r["rtId"]!.Value<string>() == rollup.ToString());
    }

    private async Task InsertHourlyAsync(OctoObjectId raw, OctoObjectId series)
    {
        var points = new List<(DateTime, double)>();
        foreach (var (start, end) in FilledGasDays)
        {
            for (var t = start; t < end; t = t.AddHours(1))
            {
                points.Add((t, 1d));
            }
        }

        await _builder.InsertPointsAsync(raw, series, points);
    }

    private async Task SeedDayStartAsync(OctoObjectId rollupRtId, long dayStartOffsetMs)
    {
        var repository = fixture.GetSystemContext().GetSystemTenantRepository();
        using var session = await repository.GetSessionAsync();
        var entity = await repository.GetRtEntityByRtIdAsync<RtRollupArchive>(session, rollupRtId);
        entity.Should().NotBeNull();
        entity!.DayStartOffsetMs = dayStartOffsetMs;
        await repository.UpdateOneRtEntityByIdAsync(session, rollupRtId, entity);
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value,
    };

    private static bool IsLocalSixOClock(DateTime utc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.FindSystemTimeZoneById(Zone));
        return local.TimeOfDay == TimeSpan.FromHours(6);
    }

    private Task<ExecutionResult> ExecuteAsync(string query, object variables) =>
        fixture.ExecuteGraphQlAsync(query, JsonSerializer.Serialize(variables), StreamDataFixture.StreamDataAdminPrincipal);

    private JToken Data(ExecutionResult result) => JObject.Parse(fixture.SerializeGraphQl(result))["data"]!;
}
