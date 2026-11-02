using Meshmakers.Octo.ConstructionKit.Contracts;
using System.Security.Claims;
using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
///     AB#7812: <c>anchor.&lt;path&gt;</c> columns of stream-data queries show attributes of the anchor entity of a row (the
///     entity the row's rtId points at) next to the archive's values. Covers the rollup, raw and time-range archives, the
///     persisted and the transient entry point, enum names, the caller's data permissions, unknown paths and the
///     display-only limit.
/// </summary>
[Collection(StreamDataAnchorColumnsCollection.Name)]
public class StreamDataAnchorColumnsTests(AnchorColumnsStreamDataFixture fixture, ITestOutputHelper output)
{
    private static readonly ClaimsPrincipal Caller = AnchorColumnsStreamDataFixture.Plain;

    // ─── AC 1: rollup archive, persisted query ───────────────────────────────

    [Fact]
    public async Task PersistedQuery_OnARollup_ReturnsTheAnchorAttributesPerRow()
    {
        fixture.OutputHelper = output;
        var queryRtId = await CreatePersistedQueryAsync(fixture.RollupArchive,
            """["window_start", "voltage_sum", "anchor.meteringPointNumber", "anchor.operatingStatus"]""");

        var result = await fixture.ExecuteGraphQlAsync($$"""
            { streamData { streamDataQuery(rtId: "{{queryRtId}}") { items {
                columns { attributePath attributeValueType }
                rows(first: 50) { items { cells(first: 20) { items { attributePath value } } } }
            } } } }
            """, user: Caller);

        var json = JObject.Parse(fixture.SerializeGraphQl(result));
        result.Errors.Should().BeNullOrEmpty(json.ToString());

        // The column list reports the CK type of the anchor attribute.
        var columns = json.SelectTokens("data.streamData.streamDataQuery.items[0].columns[*]").ToList();
        columns.Select(c => c.Value<string>("attributePath")).Should().Equal(
            "window_start", "voltage_sum", "anchor.meteringPointNumber", "anchor.operatingStatus");
        columns.Single(c => c.Value<string>("attributePath") == "anchor.meteringPointNumber")
            .Value<string>("attributeValueType").Should().Be("STRING");

        // Two registers (metering points) show as two rows with different values; the enum is a name, not a key.
        var rows = ReadRows(json, "data.streamData.streamDataQuery.items[0].rows.items");
        rows.Should().HaveCount(2);
        rows.Select(r => r["anchor.meteringPointNumber"]!.Value<string>()).Should().BeEquivalentTo("AT-0001", "AT-0002");
        var byNumber = rows.ToDictionary(r => r["anchor.meteringPointNumber"]!.Value<string>()!);
        byNumber["AT-0001"]["anchor.operatingStatus"]!.Value<string>().Should().Be("OK");
        byNumber["AT-0002"]["anchor.operatingStatus"]!.Value<string>().Should().Be("Maintenance");
        byNumber["AT-0001"]["voltage_sum"]!.Value<double>().Should().Be(20.0);
        byNumber["AT-0002"]["voltage_sum"]!.Value<double>().Should().Be(40.0);
    }

    // ─── AC 2 / 3 / 9: raw and time-range archive, transient query, cells ────

    [Theory]
    [InlineData("raw")]
    [InlineData("timeRange")]
    public async Task TransientQuery_OnRawAndTimeRangeArchives_ReturnsTheAnchorAttributes(string kind)
    {
        fixture.OutputHelper = output;
        var archive = kind == "raw" ? fixture.RawArchive : fixture.TimeRangeArchive;

        var rows = await RunTransientAsync(archive,
            """["voltage", "anchor.meteringPointNumber", "anchor.operatingStatus"]""");

        rows.Should().HaveCount(4);
        foreach (var row in rows)
        {
            var number = row["anchor.meteringPointNumber"]!.Value<string>();
            number.Should().BeOneOf("AT-0001", "AT-0002");
            row["anchor.operatingStatus"]!.Value<string>().Should().Be(number == "AT-0001" ? "OK" : "Maintenance");
            row["voltage"]!.Value<double>().Should().Be(number == "AT-0001" ? 10.0 : 20.0);
        }
    }

    [Fact]
    public async Task TransientQuery_ReturnsTheSameValuesAsThePersistedQuery()
    {
        fixture.OutputHelper = output;
        var columns = """["voltage", "anchor.meteringPointNumber", "anchor.operatingStatus"]""";
        var queryRtId = await CreatePersistedQueryAsync(fixture.RawArchive, columns);

        var persisted = await fixture.ExecuteGraphQlAsync($$"""
            { streamData { streamDataQuery(rtId: "{{queryRtId}}") { items {
                rows(first: 50) { items { cells(first: 20) { items { attributePath value } } } }
            } } } }
            """, user: Caller);
        var persistedJson = JObject.Parse(fixture.SerializeGraphQl(persisted));
        persisted.Errors.Should().BeNullOrEmpty(persistedJson.ToString());

        var persistedRows = ReadRows(persistedJson, "data.streamData.streamDataQuery.items[0].rows.items");
        var transientRows = await RunTransientAsync(fixture.RawArchive, columns);

        Canonical(persistedRows).Should().Equal(Canonical(transientRows));
    }

    [Fact]
    public async Task EnumCells_FollowResolveEnumValuesToNames()
    {
        fixture.OutputHelper = output;

        var result = await fixture.ExecuteGraphQlAsync($$"""
            { streamData { transientStreamDataQuery { simple(
                archiveRtId: "{{fixture.RawArchive}}", columnPaths: ["anchor.operatingStatus"]) { items {
                rows(first: 50) { items { cells(first: 5, resolveEnumValuesToNames: false) { items { attributePath value } } } }
            } } } } }
            """, user: Caller);
        var json = JObject.Parse(fixture.SerializeGraphQl(result));
        result.Errors.Should().BeNullOrEmpty(json.ToString());

        var rows = ReadRows(json, "data.streamData.transientStreamDataQuery.simple.items[0].rows.items");
        rows.Select(r => r["anchor.operatingStatus"]!.Value<int>()).Should().BeEquivalentTo(new[] { 1, 1, 2, 2 });
    }

    [Fact]
    public async Task AnchorColumns_KeepTheirPlaceInTheColumnOrder()
    {
        fixture.OutputHelper = output;

        var result = await fixture.ExecuteGraphQlAsync($$"""
            { streamData { transientStreamDataQuery { simple(
                archiveRtId: "{{fixture.RawArchive}}", columnPaths: ["anchor.meteringPointNumber", "voltage", "anchor.operatingStatus"]) { items {
                rows(first: 1) { items { cells(first: 5) { items { attributePath } } } }
            } } } } }
            """, user: Caller);
        var json = JObject.Parse(fixture.SerializeGraphQl(result));
        result.Errors.Should().BeNullOrEmpty(json.ToString());

        json.SelectTokens("data.streamData.transientStreamDataQuery.simple.items[0].rows.items[0].cells.items[*].attributePath")
            .Select(t => t.Value<string>()).Should().Equal("anchor.meteringPointNumber", "voltage", "anchor.operatingStatus");
    }

    // ─── AC 4: permissions ───────────────────────────────────────────────────

    [Fact]
    public async Task CallerWithoutReadRightsOnAnAnchor_GetsNullCellsAndNothingElse()
    {
        fixture.OutputHelper = output;

        var plain = await RunTransientRawJsonAsync(fixture.AssetArchive, """["name", "anchor.name", "anchor.description"]""",
            AnchorColumnsStreamDataFixture.Plain);
        var plainRows = ReadRows(JObject.Parse(plain), "data.streamData.transientStreamDataQuery.simple.items[0].rows.items");

        plainRows.Should().HaveCount(3, "the stream-data values follow the existing rules: the caller may read the type");
        var byRow = plainRows.ToDictionary(r => r["name"]!.Value<string>()!);
        byRow["row of the metering point"]["anchor.name"]!.Value<string>().Should().Be("Metering point 1");
        byRow["row of the facility"]["anchor.name"]!.Type.Should().Be(JTokenType.Null);
        byRow["row of the facility"]["anchor.description"]!.Type.Should().Be(JTokenType.Null);
        byRow["row without an anchor"]["anchor.name"]!.Type.Should().Be(JTokenType.Null);
        plain.Should().NotContain("Secret facility").And.NotContain("protected by a data policy");

        var reader = await RunTransientAsync(fixture.AssetArchive, """["name", "anchor.name"]""",
            AnchorColumnsStreamDataFixture.FacilityReader);
        reader.ToDictionary(r => r["name"]!.Value<string>()!)["row of the facility"]["anchor.name"]!.Value<string>()
            .Should().Be("Secret facility");
    }

    // ─── AC 5: unknown path ──────────────────────────────────────────────────

    [Fact]
    public async Task UnknownAnchorPath_FailsWithAValidationErrorNamingColumnAndType()
    {
        fixture.OutputHelper = output;

        var result = await fixture.ExecuteGraphQlAsync($$"""
            { streamData { transientStreamDataQuery { simple(
                archiveRtId: "{{fixture.RawArchive}}", columnPaths: ["voltage", "anchor.doesNotExist"]) { items { columns { attributePath } } } } } }
            """, user: Caller);

        AssertValidationError(result, "anchor.doesNotExist", AnchorColumnsStreamDataFixture.MeteringPointType);
    }

    [Fact]
    public async Task UnknownAnchorPath_InAPersistedQuery_FailsWhenTheRowsRun()
    {
        fixture.OutputHelper = output;
        var queryRtId = await CreatePersistedQueryAsync(fixture.RawArchive, """["voltage", "anchor.doesNotExist"]""");

        var result = await fixture.ExecuteGraphQlAsync($$"""
            { streamData { streamDataQuery(rtId: "{{queryRtId}}") { items { rows(first: 5) { items { rtId } } } } } }
            """, user: Caller);

        AssertValidationError(result, "anchor.doesNotExist", AnchorColumnsStreamDataFixture.MeteringPointType);
    }

    // ─── AC 6: display only ──────────────────────────────────────────────────

    public static TheoryData<string> DisplayOnlyViolations => new()
    {
        // field filter (also an operator without a value)
        """simple(archiveRtId: "{0}", columnPaths: ["voltage"], fieldFilter: [{ attributePath: "anchor.meteringPointNumber", operator: EQUALS, comparisonValue: "AT-0001" }]) { items { rows { items { rtId } } } }""",
        """simple(archiveRtId: "{0}", columnPaths: ["voltage"], fieldFilter: [{ attributePath: "anchor.meteringPointNumber", operator: IS_NULL }]) { items { rows { items { rtId } } } }""",
        // sorting
        """simple(archiveRtId: "{0}", columnPaths: ["voltage"], sortOrder: [{ attributePath: "anchor.meteringPointNumber", sortOrder: ASCENDING }]) { items { rows { items { rtId } } } }""",
        """simple(archiveRtId: "{0}", columnPaths: ["voltage"]) { items { rows(sortOrder: [{ attributePath: "anchor.meteringPointNumber", sortOrder: ASCENDING }]) { items { rtId } } } }""",
        // aggregation, grouping, downsampling
        """aggregation(archiveRtId: "{0}", columnPaths: [{ attributePath: "anchor.meteringPointNumber", aggregationType: COUNT }]) { items { rows { items { rtId } } } }""",
        """groupingAggregation(archiveRtId: "{0}", groupByColumnPaths: ["anchor.meteringPointNumber"], columnPaths: [{ attributePath: "voltage", aggregationType: SUM }]) { items { rows { items { rtId } } } }""",
        """downsampling(archiveRtId: "{0}", columnPaths: [{ attributePath: "anchor.meteringPointNumber", aggregationType: COUNT }], limit: 4, from: "2026-03-01T00:00:00Z", to: "2026-03-02T00:00:00Z") { items { rows { items { rtId } } } }""",
        // a simple query switched to downsampling mode
        """simple(archiveRtId: "{0}", columnPaths: ["voltage", "anchor.meteringPointNumber"], arg: { queryMode: DOWNSAMPLING, from: "2026-03-01T00:00:00Z", to: "2026-03-02T00:00:00Z", limit: 4 }) { items { rows { items { rtId } } } }""",
        // the aggregations sub-connection
        """simple(archiveRtId: "{0}", columnPaths: ["voltage"]) { items { aggregations(aggregations: { countAttributePaths: ["anchor.meteringPointNumber"] }) { items { countStatistics { attributePath } } } } }"""
    };

    [Theory]
    [MemberData(nameof(DisplayOnlyViolations))]
    public async Task AnchorColumn_OutsideAColumnOfASimpleQuery_IsRefused(string subQuery)
    {
        fixture.OutputHelper = output;

        var result = await fixture.ExecuteGraphQlAsync(
            "{ streamData { transientStreamDataQuery { " + subQuery.Replace("{0}", fixture.RawArchive.ToString()) + " } } }",
            user: Caller);

        var json = fixture.SerializeGraphQl(result);
        result.Errors.Should().NotBeNull(json);
        result.Errors!.Select(e => e.Message + " " + e.InnerException?.Message).Should().Contain(
            m => m.Contains("anchor.meteringPointNumber") && m.Contains("display-only"), json);
    }

    [Fact]
    public async Task PersistedQuery_WithAnAnchorFilter_IsRefusedWhenRun()
    {
        fixture.OutputHelper = output;
        var queryRtId = await StoreAsImportedAsync(fixture.RawArchive, q =>
        {
            q.Columns = new AttributeStringValueList(["voltage"]);
            q.FieldFilter = new AttributeRecordValueList<RtFieldFilterRecord>
            {
                new RtFieldFilterRecord
                {
                    AttributePath = "anchor.meteringPointNumber", Operator = RtFieldFilterOperatorEnum.Equals,
                    ComparisonValue = "AT-0001"
                }
            };
        });

        var result = await fixture.ExecuteGraphQlAsync($$"""
            { streamData { streamDataQuery(rtId: "{{queryRtId}}") { items { rows(first: 5) { items { rtId } } } } } }
            """, user: Caller);

        var json = fixture.SerializeGraphQl(result);
        result.Errors.Should().NotBeNull(json);
        json.Should().Contain("display-only");
    }

    // ─── AC 8: no regression ─────────────────────────────────────────────────

    [Fact]
    public async Task QueryWithoutAnchorColumns_ReturnsTheSameRowsAsBefore()
    {
        fixture.OutputHelper = output;

        var rows = await RunTransientAsync(fixture.RawArchive, """["voltage", "rtId"]""");

        rows.Should().HaveCount(4);
        rows.Select(r => r["voltage"]!.Value<double>()).Should().BeEquivalentTo(new[] { 10.0, 10.0, 20.0, 20.0 });
        rows.All(r => r.Properties().All(p => !p.Name.StartsWith("anchor.", StringComparison.Ordinal))).Should().BeTrue();
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    private Task<List<JObject>> RunTransientAsync(OctoObjectId archive, string columnPaths, ClaimsPrincipal? user = null) =>
        RunTransientAsync(archive.ToString(), columnPaths, user ?? Caller);

    private async Task<List<JObject>> RunTransientAsync(string archive, string columnPaths, ClaimsPrincipal user)
    {
        var json = await RunTransientRawJsonAsync(archive, columnPaths, user);
        return ReadRows(JObject.Parse(json), "data.streamData.transientStreamDataQuery.simple.items[0].rows.items");
    }

    private Task<string> RunTransientRawJsonAsync(OctoObjectId archive, string columnPaths, ClaimsPrincipal user) =>
        RunTransientRawJsonAsync(archive.ToString(), columnPaths, user);

    private async Task<string> RunTransientRawJsonAsync(string archive, string columnPaths, ClaimsPrincipal user)
    {
        var result = await fixture.ExecuteGraphQlAsync($$"""
            { streamData { transientStreamDataQuery { simple(archiveRtId: "{{archive}}", columnPaths: {{columnPaths}}) { items {
                rows(first: 50) { items { cells(first: 20) { items { attributePath value } } } }
            } } } } }
            """, user: user);
        var json = fixture.SerializeGraphQl(result);
        output.WriteLine(json);
        result.Errors.Should().BeNullOrEmpty(json);
        return json;
    }

    /// <summary>One object per row: the attribute path of each cell as the key.</summary>
    private static List<JObject> ReadRows(JObject json, string path) =>
        json.SelectTokens(path + "[*]")
            .Select(row =>
            {
                var obj = new JObject();
                foreach (var cell in row.SelectTokens("cells.items[*]"))
                {
                    obj[cell.Value<string>("attributePath")!] = cell["value"];
                }

                return obj;
            })
            .ToList();

    private static List<string> Canonical(IEnumerable<JObject> rows) =>
        rows.Select(r => r.ToString(Newtonsoft.Json.Formatting.None)).OrderBy(s => s, StringComparer.Ordinal).ToList();

    private async Task<string> CreatePersistedQueryAsync(OctoObjectId archive, string columns)
    {
        // Saved through the GraphQL mutation, so that the save-time guard of stored queries (AB#8049) is part of the test.
        var create = await fixture.ExecuteGraphQlAsync($$"""
            mutation {
              runtime { systemSimpleSdQuerys {
                create(entities: [{ name: "anchor-sd", queryCkTypeId: "{{AnchorColumnsStreamDataFixture.MeteringPointType}}",
                                    archiveRtId: "{{archive}}", columns: {{columns}} }]) { rtId }
              } }
            }
            """);
        var createJson = fixture.SerializeGraphQl(create);
        create.Errors.Should().BeNullOrEmpty(createJson);
        return JObject.Parse(createJson).SelectToken("data.runtime.systemSimpleSdQuerys.create[0].rtId")!.Value<string>()!;
    }

    private async Task<string> StoreAsImportedAsync(OctoObjectId archive, Action<RtSimpleSdQuery> configure)
    {
        var repository = fixture.GetSystemContext().GetSystemTenantRepository();
        var query = await repository.CreateTransientRtEntityAsync<RtSimpleSdQuery>();
        query.Name = "anchor-sd-imported";
        query.QueryCkTypeId = AnchorColumnsStreamDataFixture.MeteringPointType;
        query.ArchiveRtId = archive.ToString();
        configure(query);
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        await repository.InsertOneRtEntityAsync(session, query);
        await session.CommitTransactionAsync();
        return query.RtId.ToString();
    }

    private void AssertValidationError(ExecutionResult result, string column, string ckType)
    {
        var json = fixture.SerializeGraphQl(result);
        output.WriteLine(json);
        result.Errors.Should().NotBeNull(json);
        var error = result.Errors!.First();
        error.Code.Should().Be("ASSET1004", json);
        error.Message.Should().Contain(column).And.Contain(ckType);
    }
}
