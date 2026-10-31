using System.Globalization;
using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Newtonsoft.Json.Linq;
using Npgsql;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
/// AB#7919 — the step integral of AB#7918 through the GraphQL entry points against a real CrateDB: the transient
/// <c>aggregation</c> / <c>groupingAggregation</c> queries (<c>aggregationType: INTEGRAL</c>) and persisted
/// <c>System/AggregationSdQuery</c> / <c>System/GroupingAggregationSdQuery</c> entities with
/// <c>AggregationType = Integral</c> executed via <c>streamDataQuery</c>, plus the structured refusals.
/// </summary>
/// <remarks>
/// Dedicated raw archive on <c>MeteringPoint</c> (Voltage stands in for a power column in W), window
/// [10:00, 11:00) UTC on 2026-06-01, far from the fixture's canonical data:
/// <code>
/// rtId A (5-s rows):     10:00:00 … 10:29:55 = 1200 W, 10:30:00 … 10:59:55 = 600 W
///                        → 1200 × 0.5 h + 600 × 0.5 h = 900 W·h
/// rtId B (on-change):    09:30 = 1000 W (carry-in), 10:15 = 0 W, 10:45 = 2000 W
///                        → 1000 × 0.25 h + 0 × 0.5 h + 2000 × 0.25 h = 750 W·h
/// sum over both rtIds    = 1650 W·h
/// </code>
/// </remarks>
[Collection(StreamDataMutatingCollection.Name)]
public class StreamDataIntegralQueryTests(StreamDataFixture fixture, ITestOutputHelper output)
{
    private static readonly DateTime WindowStart = new(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowEnd = new(2026, 6, 1, 11, 0, 0, DateTimeKind.Utc);
    private const double ExpectedA = 900d;
    private const double ExpectedB = 750d;
    private const double Tolerance = 1e-9;

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static IntegralArchive? _archive;

    private string From => WindowStart.ToString("O", CultureInfo.InvariantCulture);
    private string To => WindowEnd.ToString("O", CultureInfo.InvariantCulture);

    [Fact]
    public async Task TransientAggregation_Integral_SumsBothRtIdsInValueTimesHours()
    {
        fixture.OutputHelper = output;
        var archive = await EnsureArchiveAsync();

        var result = await fixture.ExecuteGraphQlAsync($$"""
            { streamData { transientStreamDataQuery { aggregation(
                archiveRtId: "{{archive.RtId}}"
                columnPaths: [{ attributePath: "Voltage", aggregationType: INTEGRAL }]
                arg: { from: "{{From}}", to: "{{To}}", queryMode: DEFAULT } first: 10) {
              items {
                columns { attributePath aggregationType attributeValueType }
                rows(first: 10) { items { cells(first: 10) { items { attributePath value } } } }
              } } } } }
            """);
        var json = AssertSucceeded(result);

        var item = json.SelectToken("data.streamData.transientStreamDataQuery.aggregation.items[0]")!;
        var column = item["columns"]!.Single();
        column["aggregationType"]!.Value<string>().Should().Be("INTEGRAL");
        column["attributeValueType"]!.Value<string>().Should().Be("DOUBLE");

        var rows = item.SelectToken("rows.items")!.ToList();
        rows.Should().ContainSingle("an aggregation without grouping returns one row");
        IntegralCell(rows[0]).Should().BeApproximately(ExpectedA + ExpectedB, Tolerance);
    }

    [Fact]
    public async Task TransientGroupingAggregation_Integral_PerRtId()
    {
        fixture.OutputHelper = output;
        var archive = await EnsureArchiveAsync();

        var result = await fixture.ExecuteGraphQlAsync($$"""
            { streamData { transientStreamDataQuery { groupingAggregation(
                archiveRtId: "{{archive.RtId}}"
                groupByColumnPaths: ["rtId"]
                columnPaths: [{ attributePath: "Voltage", aggregationType: INTEGRAL }]
                arg: { from: "{{From}}", to: "{{To}}", queryMode: DEFAULT } first: 10) {
              items { rows(first: 10) { items { cells(first: 10) { items { attributePath value } } } } }
            } } } }
            """);
        var json = AssertSucceeded(result);

        var rows = json.SelectToken("data.streamData.transientStreamDataQuery.groupingAggregation.items[0].rows.items")!
            .ToList();
        IntegralByRtId(rows).Should().BeEquivalentTo(new Dictionary<string, double>
        {
            [archive.RtIdA] = ExpectedA,
            [archive.RtIdB] = ExpectedB,
        }, o => o.Using<double>(c => c.Subject.Should().BeApproximately(c.Expectation, Tolerance)).WhenTypeIs<double>());
    }

    [Fact]
    public async Task PersistedAggregationSdQuery_Integral_ReportsIntegralColumnAndValue()
    {
        fixture.OutputHelper = output;
        var archive = await EnsureArchiveAsync();
        var queryRtId = await CreatePersistedQueryAsync("systemAggregationSdQuerys", archive,
            """columns: [{ attributePath: "Voltage", aggregationType: INTEGRAL }]""");

        var result = await fixture.ExecuteGraphQlAsync($$"""
            { streamData { streamDataQuery(rtId: "{{queryRtId}}") { items {
                columns { attributePath aggregationType attributeValueType }
                rows(arg: { from: "{{From}}", to: "{{To}}", queryMode: DEFAULT }, first: 10) {
                  items { cells(first: 10) { items { attributePath value } } } }
            } } } }
            """);
        var json = AssertSucceeded(result);

        var item = json.SelectToken("data.streamData.streamDataQuery.items[0]")!;
        var column = item["columns"]!.Single();
        column["aggregationType"]!.Value<string>().Should().Be("INTEGRAL");
        column["attributeValueType"]!.Value<string>().Should().Be("DOUBLE");

        var rows = item.SelectToken("rows.items")!.ToList();
        rows.Should().ContainSingle();
        IntegralCell(rows[0]).Should().BeApproximately(ExpectedA + ExpectedB, Tolerance);
    }

    [Fact]
    public async Task PersistedGroupingAggregationSdQuery_Integral_PerRtId()
    {
        fixture.OutputHelper = output;
        var archive = await EnsureArchiveAsync();
        var queryRtId = await CreatePersistedQueryAsync("systemGroupingAggregationSdQuerys", archive,
            """groupingColumns: ["rtId"], columns: [{ attributePath: "Voltage", aggregationType: INTEGRAL }]""");

        var result = await fixture.ExecuteGraphQlAsync($$"""
            { streamData { streamDataQuery(rtId: "{{queryRtId}}") { items {
                columns { attributePath aggregationType }
                rows(arg: { from: "{{From}}", to: "{{To}}", queryMode: DEFAULT }, first: 10) {
                  items { cells(first: 10) { items { attributePath value } } } }
            } } } }
            """);
        var json = AssertSucceeded(result);

        var item = json.SelectToken("data.streamData.streamDataQuery.items[0]")!;
        item["columns"]!.Select(c => c["aggregationType"]!.Value<string>())
            .Should().Equal("NONE", "INTEGRAL");
        IntegralByRtId(item.SelectToken("rows.items")!.ToList()).Should().BeEquivalentTo(
            new Dictionary<string, double> { [archive.RtIdA] = ExpectedA, [archive.RtIdB] = ExpectedB },
            o => o.Using<double>(c => c.Subject.Should().BeApproximately(c.Expectation, Tolerance)).WhenTypeIs<double>());
    }

    [Fact]
    public async Task TransientAggregation_IntegralOnATextColumn_IsAStructuredError()
    {
        fixture.OutputHelper = output;
        var archive = await EnsureArchiveAsync();

        var result = await fixture.ExecuteGraphQlAsync($$"""
            { streamData { transientStreamDataQuery { aggregation(
                archiveRtId: "{{archive.RtId}}"
                columnPaths: [{ attributePath: "MeteringPointNumber", aggregationType: INTEGRAL }]
                arg: { from: "{{From}}", to: "{{To}}", queryMode: DEFAULT } first: 10) {
              items { rows(first: 10) { items { cells(first: 10) { items { value } } } } }
            } } } }
            """);

        var error = AssertSingleCodedError(result, Statics.GraphQlModelValidationErrors);
        error.Message.Should().Contain("MeteringPointNumber").And.Contain("numeric");
    }

    [Fact]
    public async Task TransientAggregation_IntegralOnATextPlatformField_IsAStructuredError()
    {
        // row_origin resolves as a data-stream field but has no CK attribute and is text (review of PR #120).
        fixture.OutputHelper = output;
        var archive = await EnsureArchiveAsync();

        var result = await fixture.ExecuteGraphQlAsync($$"""
            { streamData { transientStreamDataQuery { aggregation(
                archiveRtId: "{{archive.RtId}}"
                columnPaths: [{ attributePath: "row_origin", aggregationType: INTEGRAL }]
                arg: { from: "{{From}}", to: "{{To}}", queryMode: DEFAULT } first: 10) {
              items { rows(first: 10) { items { cells(first: 10) { items { value } } } } }
            } } } }
            """);

        AssertSingleCodedError(result, Statics.GraphQlModelValidationErrors).Message.Should().Contain("row_origin");
    }

    [Fact]
    public async Task PersistedAggregationSdQuery_IntegralOnABuiltInColumn_IsAStructuredError()
    {
        fixture.OutputHelper = output;
        var archive = await EnsureArchiveAsync();
        var queryRtId = await CreatePersistedQueryAsync("systemAggregationSdQuerys", archive,
            """columns: [{ attributePath: "rtId", aggregationType: INTEGRAL }]""");

        var result = await fixture.ExecuteGraphQlAsync($$"""
            { streamData { streamDataQuery(rtId: "{{queryRtId}}") { items {
                rows(arg: { from: "{{From}}", to: "{{To}}", queryMode: DEFAULT }, first: 10) { items { timestamp } }
            } } } }
            """);

        AssertSingleCodedError(result, Statics.GraphQlModelValidationErrors).Message.Should().Contain("rtId");
    }

    [Fact]
    public async Task TransientAggregation_IntegralWithoutWindow_IsAStructuredError()
    {
        fixture.OutputHelper = output;
        var archive = await EnsureArchiveAsync();

        var result = await fixture.ExecuteGraphQlAsync($$"""
            { streamData { transientStreamDataQuery { aggregation(
                archiveRtId: "{{archive.RtId}}"
                columnPaths: [{ attributePath: "Voltage", aggregationType: INTEGRAL }]
                first: 10) {
              items { rows(first: 10) { items { cells(first: 10) { items { value } } } } }
            } } } }
            """);

        var json = fixture.SerializeGraphQl(result);
        result.Errors.Should().NotBeNullOrEmpty(json);
        var error = result.Errors!.Single();
        error.Code.Should().NotBeNullOrEmpty(json).And.NotBe("GENERAL", json);
        error.Message.Should().Contain("From and To", json);
    }

    [Fact]
    public async Task TransientDownsampling_Integral_IsAStructuredError()
    {
        fixture.OutputHelper = output;
        var archive = await EnsureArchiveAsync();

        var result = await fixture.ExecuteGraphQlAsync($$"""
            { streamData { transientStreamDataQuery { downsampling(
                archiveRtId: "{{archive.RtId}}"
                columnPaths: [{ attributePath: "Voltage", aggregationType: INTEGRAL }]
                limit: 4, from: "{{From}}", to: "{{To}}") {
              items { rows { items { timestamp } } }
            } } } }
            """);

        AssertSingleCodedError(result, Statics.GraphQlModelValidationErrors).Message.Should().Contain("downsampling");
    }

    // ---------------------------------------------------------------------------------------------

    private sealed record IntegralArchive(string RtId, string RtIdA, string RtIdB);

    private JObject AssertSucceeded(ExecutionResult result)
    {
        var json = fixture.SerializeGraphQl(result);
        output.WriteLine(json);
        result.Errors.Should().BeNullOrEmpty(json);
        return JObject.Parse(json);
    }

    private ExecutionError AssertSingleCodedError(ExecutionResult result, string code)
    {
        var json = fixture.SerializeGraphQl(result);
        output.WriteLine(json);
        result.Errors.Should().NotBeNullOrEmpty(json);
        var error = result.Errors!.Single();
        error.Code.Should().Be(code, json);
        return error;
    }

    private static double IntegralCell(JToken row)
    {
        var cell = row.SelectToken("cells.items")!
            .Single(c => c["attributePath"]!.Value<string>()!.EndsWith("_integral", StringComparison.OrdinalIgnoreCase));
        return Convert.ToDouble(cell["value"]!.ToString(), CultureInfo.InvariantCulture);
    }

    private static Dictionary<string, double> IntegralByRtId(IEnumerable<JToken> rows)
    {
        return rows.ToDictionary(
            row => row.SelectToken("cells.items")!
                .Single(c => string.Equals(c["attributePath"]!.Value<string>(), "rtId", StringComparison.OrdinalIgnoreCase))
                ["value"]!.ToString(),
            IntegralCell);
    }

    private async Task<string> CreatePersistedQueryAsync(string collection, IntegralArchive archive, string fields)
    {
        var create = await fixture.ExecuteGraphQlAsync($$"""
            mutation {
              runtime { {{collection}} {
                create(entities: [{ name: "integral-{{Guid.NewGuid():N}}", queryCkTypeId: "{{fixture.TestCkTypeId}}",
                                    archiveRtId: "{{archive.RtId}}", {{fields}} }]) { rtId }
              } }
            }
            """);
        var createJson = fixture.SerializeGraphQl(create);
        create.Errors.Should().BeNullOrEmpty(createJson);
        return JObject.Parse(createJson).SelectToken($"data.runtime.{collection}.create[0].rtId")!.Value<string>()!;
    }

    private async Task<IntegralArchive> EnsureArchiveAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (_archive != null && fixture.Provider != null)
            {
                return _archive;
            }

            var systemContext = fixture.GetSystemContext();
            var tenantRepository = systemContext.GetSystemTenantRepository();
            var archive = new RtRawArchive
            {
                RtWellKnownName = $"IntegralArchive{Guid.NewGuid():N}",
                TargetCkTypeId = fixture.TestCkTypeId,
                Status = RtCkArchiveStatusEnum.Created,
                Columns = new AttributeRecordValueList<RtCkArchiveColumnRecord>
                {
                    new() { Path = "Voltage", Indexed = true, Required = false },
                    new() { Path = "MeteringPointNumber", Indexed = false, Required = false },
                },
            };
            using (var session = await tenantRepository.GetSessionAsync())
            {
                session.StartTransaction();
                await tenantRepository.InsertOneRtEntityAsync(session, archive);
                await session.CommitTransactionAsync();
            }

            var tenantContext = await systemContext.FindTenantContextAsync(systemContext.TenantId);
            await tenantContext.GetArchiveLifecycleService()!.ActivateAsync(archive.RtId);

            var ckTypeId = new RtCkId<CkTypeId>(fixture.TestCkTypeId);
            var rtIdA = OctoObjectId.GenerateNewId();
            var rtIdB = OctoObjectId.GenerateNewId();
            var points = new List<StreamDataPoint>();
            for (var t = WindowStart; t < WindowEnd; t = t.AddSeconds(5))
            {
                points.Add(Point(rtIdA, ckTypeId, t, t < WindowStart.AddMinutes(30) ? 1200d : 600d));
            }

            points.Add(Point(rtIdB, ckTypeId, WindowStart.AddMinutes(-30), 1000d));
            points.Add(Point(rtIdB, ckTypeId, WindowStart.AddMinutes(15), 0d));
            points.Add(Point(rtIdB, ckTypeId, WindowStart.AddMinutes(45), 2000d));

            await tenantContext.GetStreamDataRepository()!.InsertAsync(archive.RtId, points);
            await RefreshAsync($"\"{fixture.StreamDataTenantId}\".\"archive_{archive.RtId}\"");

            output.WriteLine($"Integral archive={archive.RtId} A={rtIdA} B={rtIdB} rows={points.Count}");
            _archive = new IntegralArchive(archive.RtId.ToString(), rtIdA.ToString(), rtIdB.ToString());
            return _archive;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static StreamDataPoint Point(OctoObjectId rtId, RtCkId<CkTypeId> ckTypeId, DateTime timestamp, double power)
    {
        return new StreamDataPoint
        {
            RtId = rtId,
            CkTypeId = ckTypeId,
            Timestamp = timestamp,
            RtWellKnownName = $"Integral{rtId}",
            Attributes = new Dictionary<string, object?>
            {
                ["voltage"] = power,
                ["meteringPointNumber"] = $"AT00{rtId}",
            },
        };
    }

    private async Task RefreshAsync(string qualifiedTable)
    {
        await using var conn = new NpgsqlConnection(fixture.CrateDbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"REFRESH TABLE {qualifiedTable}", conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
