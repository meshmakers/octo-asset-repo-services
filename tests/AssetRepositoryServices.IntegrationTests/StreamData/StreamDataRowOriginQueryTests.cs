using System.Text.Json;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
/// AB#7616 through GraphQL against a real CrateDB: a stream-data query returns the origin of every
/// time-range row and the manual audit, filters by origin and groups by it, so a consumer counts manual and
/// automated intervals per series in one grouped aggregation.
/// </summary>
[Collection(StreamDataCollection.Name)]
public class StreamDataRowOriginQueryTests(StreamDataFixture fixture, ITestOutputHelper output)
{
    private readonly MultiSourceArchiveBuilder _builder = new(fixture);
    private static readonly DateTime Day = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private async Task<(OctoObjectId Archive, OctoObjectId Series)> ArrangeAsync()
    {
        fixture.OutputHelper = output;
        var archive = await _builder.CreateTimeRangeArchiveAsync("OriginQuery", TimeSpan.FromMinutes(15));
        var series = OctoObjectId.GenerateNewId();
        var repo = (await _builder.TenantAsync()).GetStreamDataRepository()!;
        TimeRangeStreamDataPoint Window(int i, double value, ArchiveRowOrigin origin) => new()
        {
            RtId = series,
            CkTypeId = new RtCkId<CkTypeId>(fixture.TestCkTypeId),
            From = Day.AddMinutes(15 * i),
            To = Day.AddMinutes(15 * (i + 1)),
            Attributes = new Dictionary<string, object?> { [MultiSourceArchiveBuilder.BaseColumn] = value },
            Origin = origin,
            ManualWrittenBy = origin == ArchiveRowOrigin.Manual ? "user-1" : null,
            ManualReason = origin == ArchiveRowOrigin.Manual ? "meter exchanged" : null
        };
        await repo.InsertTimeRangeAsync(archive,
            [Window(0, 1, ArchiveRowOrigin.Automated), Window(1, 2, ArchiveRowOrigin.Manual), Window(2, 4, ArchiveRowOrigin.Automated)],
            TestContext.Current.CancellationToken);
        await _builder.RefreshAsync(archive);
        return (archive, series);
    }

    private async Task<List<Dictionary<string, string?>>> RowsAsync(string variant, string arguments)
    {
        var query = $$"""
            {
                streamData {
                    transientStreamDataQuery {
                        {{variant}}(
                            {{arguments}}
                            first: 10
                        ) {
                            items {
                                rows(first: 10) {
                                    items {
                                        cells(first: 10) { items { attributePath value } }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            """;
        var result = await fixture.ExecuteGraphQlAsync(query);
        var json = fixture.SerializeGraphQl(result);
        output.WriteLine(json);
        result.Errors.Should().BeNullOrEmpty();

        return JsonDocument.Parse(json).RootElement
            .GetProperty("data").GetProperty("streamData").GetProperty("transientStreamDataQuery").GetProperty(variant)
            .GetProperty("items").EnumerateArray().First().GetProperty("rows").GetProperty("items").EnumerateArray()
            .Select(row => row.GetProperty("cells").GetProperty("items").EnumerateArray().ToDictionary(
                c => c.GetProperty("attributePath").GetString()!,
                c => c.GetProperty("value").ValueKind == JsonValueKind.Null ? null : c.GetProperty("value").ToString()))
            .ToList();
    }

    [Fact]
    public async Task ASimpleQuery_ReturnsTheOriginAndTheManualAudit_AndFiltersByOrigin()
    {
        var (archive, _) = await ArrangeAsync();
        var columns = $"""columnPaths: ["{MultiSourceArchiveBuilder.VoltagePath}", "row_origin", "manual_written_by", "manual_reason"]""";

        var all = await RowsAsync("simple", $"""archiveRtId: "{archive}" {columns}""");
        all.Select(r => r["row_origin"]).Should().BeEquivalentTo(["automated", "manual", "automated"]);
        all.Single(r => r["row_origin"] == "manual")["manual_written_by"].Should().Be("user-1");
        all.Single(r => r["row_origin"] == "manual")["manual_reason"].Should().Be("meter exchanged");

        var manual = await RowsAsync("simple",
            $"""archiveRtId: "{archive}" {columns} fieldFilter: [{"{"} attributePath: "row_origin" operator: EQUALS comparisonValue: "manual" {"}"}]""");
        manual.Should().ContainSingle().Which["row_origin"].Should().Be("manual");
    }

    [Fact]
    public async Task AGroupedAggregation_CountsManualAndAutomatedIntervalsPerSeries()
    {
        var (archive, _) = await ArrangeAsync();

        var groups = await RowsAsync("groupingAggregation",
            $"""
            archiveRtId: "{archive}"
            groupByColumnPaths: ["rtId", "row_origin"]
            columnPaths: [
                {"{"} attributePath: "{MultiSourceArchiveBuilder.VoltagePath}", aggregationType: SUM {"}"}
                {"{"} attributePath: "{MultiSourceArchiveBuilder.VoltagePath}", aggregationType: COUNT {"}"}
            ]
            """);

        groups.Select(g => (g["row_origin"], g["voltage_sum"], g["voltage_count"])).Should().BeEquivalentTo(
            [("automated", "5", "2"), ("manual", "2", "1")]);
    }
}
