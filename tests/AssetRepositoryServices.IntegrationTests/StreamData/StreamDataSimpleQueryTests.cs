using System.Text.Json;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
/// Integration tests for stream data simple queries via the GraphQL layer.
/// These tests exercise the existing resolvers end-to-end and serve as a safety net
/// during the engine migration (AB#3364).
/// </summary>
[Collection("Sequential")]
public class StreamDataSimpleQueryTests(StreamDataFixture fixture, ITestOutputHelper output)
    : IClassFixture<StreamDataFixture>
{
    [Fact]
    public async Task TransientSimpleQuery_ReturnsAllDataPoints()
    {
        fixture.OutputHelper = output;

        var query = $$"""
            {
                streamData {
                    transientStreamDataQuery {
                        simple(
                            archiveRtId: "{{fixture.ArchiveRtIdString}}"
                            ckId: "{{fixture.TestCkTypeId}}"
                            columnPaths: ["Voltage", "Current"]
                            first: 100
                        ) {
                            items {
                                rows(first: 100) {
                                    totalCount
                                    items {
                                        rtId
                                        ckTypeId
                                        timestamp
                                        cells(first: 10) {
                                            items {
                                                attributePath
                                                value
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            """;

        var result = await fixture.ExecuteGraphQlAsync(query);
        output.WriteLine(fixture.SerializeGraphQl(result));

        result.Errors.Should().BeNullOrEmpty("GraphQL query should succeed without errors");

        var rows = GetRowsData(result);
        var totalCount = rows.GetProperty("totalCount").GetInt32();
        totalCount.Should().Be(fixture.TestDataPointCount);

        var items = rows.GetProperty("items").EnumerateArray().ToList();
        items.Should().HaveCount(fixture.TestDataPointCount);

        // Wire-format pin: attributePath values are camelCase dotted.
        foreach (var item in items.Take(3))
        {
            var cells = item.GetProperty("cells").GetProperty("items").EnumerateArray();
            foreach (var cell in cells)
            {
                var attrPath = cell.GetProperty("attributePath").GetString();
                attrPath.Should().NotBeNullOrEmpty();
                attrPath!.Should().MatchRegex("^[a-z][a-zA-Z0-9.]*$",
                    "attributePath must be camelCase dotted on the GraphQL wire");
            }
        }
    }

    [Fact]
    public async Task TransientSimpleQuery_WithTimeRange_FiltersCorrectly()
    {
        fixture.OutputHelper = output;

        // Filter to first 5 data points (10:00 - 10:12, inclusive of both endpoints)
        var from = fixture.TestDataStartTime.ToString("O");
        var to = fixture.TestDataStartTime.AddMinutes(12).ToString("O");

        var query = $$"""
            {
                streamData {
                    transientStreamDataQuery {
                        simple(
                            archiveRtId: "{{fixture.ArchiveRtIdString}}"
                            ckId: "{{fixture.TestCkTypeId}}"
                            columnPaths: ["Voltage"]
                            arg: { from: "{{from}}", to: "{{to}}", queryMode: DEFAULT }
                            first: 100
                        ) {
                            items {
                                rows(first: 100) {
                                    totalCount
                                    items {
                                        timestamp
                                    }
                                }
                            }
                        }
                    }
                }
            }
            """;

        var result = await fixture.ExecuteGraphQlAsync(query);
        output.WriteLine(fixture.SerializeGraphQl(result));

        result.Errors.Should().BeNullOrEmpty();

        var rows = GetRowsData(result);
        var totalCount = rows.GetProperty("totalCount").GetInt32();
        // 10:00, 10:03, 10:06, 10:09, 10:12 = 5 points
        totalCount.Should().Be(5);
    }

    [Fact]
    public async Task TransientSimpleQuery_WithFieldFilter_FiltersCorrectly()
    {
        fixture.OutputHelper = output;

        // Filter for Voltage = 225.0 (data points at index 10 and 11)
        var query = $$"""
            {
                streamData {
                    transientStreamDataQuery {
                        simple(
                            archiveRtId: "{{fixture.ArchiveRtIdString}}"
                            ckId: "{{fixture.TestCkTypeId}}"
                            columnPaths: ["Voltage", "Current"]
                            fieldFilter: [{ attributePath: "Voltage", operator: EQUALS, comparisonValue: "225" }]
                            first: 100
                        ) {
                            items {
                                rows(first: 100) {
                                    totalCount
                                    items {
                                        cells(first: 10) {
                                            items {
                                                attributePath
                                                value
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            """;

        var result = await fixture.ExecuteGraphQlAsync(query);
        output.WriteLine(fixture.SerializeGraphQl(result));

        result.Errors.Should().BeNullOrEmpty();

        var rows = GetRowsData(result);
        var totalCount = rows.GetProperty("totalCount").GetInt32();
        // Voltage 225.0 appears at index 10 (220+10*0.5=225) and 11 (220+11*0.5=225.5 → truncates to 225?)
        // Actually CrateDB stores doubles, so 225.0 and 225.5 are different. Only index 10 matches exactly.
        totalCount.Should().BeGreaterThan(0);

        var items = rows.GetProperty("items").EnumerateArray().ToList();
        foreach (var item in items)
        {
            var cells = item.GetProperty("cells").GetProperty("items").EnumerateArray().ToList();
            var voltageCell = cells.First(c => c.GetProperty("attributePath").GetString() == "voltage");
            // All returned rows should have Voltage matching the filter
            Convert.ToDouble(voltageCell.GetProperty("value").GetRawText()).Should().Be(225.0);
        }
    }

    [Fact]
    public async Task TransientSimpleQuery_WithSortOrder_SortsCorrectly()
    {
        fixture.OutputHelper = output;

        var query = $$"""
            {
                streamData {
                    transientStreamDataQuery {
                        simple(
                            archiveRtId: "{{fixture.ArchiveRtIdString}}"
                            ckId: "{{fixture.TestCkTypeId}}"
                            columnPaths: ["Voltage"]
                            sortOrder: [{ attributePath: "Voltage", sortOrder: DESCENDING }]
                            first: 5
                        ) {
                            items {
                                rows(first: 5) {
                                    items {
                                        cells(first: 10) {
                                            items {
                                                attributePath
                                                value
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            """;

        var result = await fixture.ExecuteGraphQlAsync(query);
        output.WriteLine(fixture.SerializeGraphQl(result));

        result.Errors.Should().BeNullOrEmpty();

        var rows = GetRowsData(result);
        var items = rows.GetProperty("items").EnumerateArray().ToList();
        items.Should().HaveCount(5);

        // First item should have the highest voltage (229.5)
        var firstCells = items[0].GetProperty("cells").GetProperty("items").EnumerateArray().ToList();
        var firstVoltage = Convert.ToDouble(firstCells.First(c =>
            c.GetProperty("attributePath").GetString() == "voltage").GetProperty("value").GetRawText());

        var lastCells = items[^1].GetProperty("cells").GetProperty("items").EnumerateArray().ToList();
        var lastVoltage = Convert.ToDouble(lastCells.First(c =>
            c.GetProperty("attributePath").GetString() == "voltage").GetProperty("value").GetRawText());

        firstVoltage.Should().BeGreaterThan(lastVoltage, "results should be sorted descending by Voltage");
    }

    [Fact]
    public async Task TransientSimpleQuery_WithPagination_PaginatesCorrectly()
    {
        fixture.OutputHelper = output;

        // Request first 5 rows in the rows sub-connection
        var query = $$"""
            {
                streamData {
                    transientStreamDataQuery {
                        simple(
                            archiveRtId: "{{fixture.ArchiveRtIdString}}"
                            ckId: "{{fixture.TestCkTypeId}}"
                            columnPaths: ["Voltage"]
                            first: 1
                        ) {
                            items {
                                rows(first: 5) {
                                    totalCount
                                    pageInfo {
                                        hasNextPage
                                        hasPreviousPage
                                        endCursor
                                    }
                                    items {
                                        timestamp
                                    }
                                }
                            }
                        }
                    }
                }
            }
            """;

        var result = await fixture.ExecuteGraphQlAsync(query);
        output.WriteLine(fixture.SerializeGraphQl(result));

        result.Errors.Should().BeNullOrEmpty();

        var rows = GetRowsData(result);
        var totalCount = rows.GetProperty("totalCount").GetInt32();
        totalCount.Should().Be(fixture.TestDataPointCount);

        var items = rows.GetProperty("items").EnumerateArray().ToList();
        items.Should().HaveCount(5);

        var pageInfo = rows.GetProperty("pageInfo");
        pageInfo.GetProperty("hasNextPage").GetBoolean().Should().BeTrue();

        // Request next page using endCursor on the rows sub-connection
        var endCursor = pageInfo.GetProperty("endCursor").GetString();
        var nextPageQuery = $$"""
            {
                streamData {
                    transientStreamDataQuery {
                        simple(
                            archiveRtId: "{{fixture.ArchiveRtIdString}}"
                            ckId: "{{fixture.TestCkTypeId}}"
                            columnPaths: ["Voltage"]
                            first: 1
                        ) {
                            items {
                                rows(first: 5, after: "{{endCursor}}") {
                                    totalCount
                                    items {
                                        timestamp
                                    }
                                }
                            }
                        }
                    }
                }
            }
            """;

        var nextResult = await fixture.ExecuteGraphQlAsync(nextPageQuery);
        output.WriteLine(fixture.SerializeGraphQl(nextResult));

        nextResult.Errors.Should().BeNullOrEmpty();

        var nextRows = GetRowsData(nextResult);
        var nextItems = nextRows.GetProperty("items").EnumerateArray().ToList();
        nextItems.Should().HaveCount(5);

        // Second page timestamps should be after first page timestamps
        var firstPageLastTimestamp = items[^1].GetProperty("timestamp").GetString();
        var secondPageFirstTimestamp = nextItems[0].GetProperty("timestamp").GetString();
        string.Compare(secondPageFirstTimestamp, firstPageLastTimestamp, StringComparison.Ordinal)
            .Should().BeGreaterThan(0, "second page should start after first page");
    }

    [Fact]
    public async Task TransientSimpleQuery_WithLimit_CapsResults()
    {
        fixture.OutputHelper = output;

        // Limit caps the total result set (rowCap), first on rows controls page size
        var query = $$"""
            {
                streamData {
                    transientStreamDataQuery {
                        simple(
                            archiveRtId: "{{fixture.ArchiveRtIdString}}"
                            ckId: "{{fixture.TestCkTypeId}}"
                            columnPaths: ["Voltage"]
                            arg: { limit: 7, queryMode: DEFAULT }
                            first: 1
                        ) {
                            items {
                                rows(first: 100) {
                                    totalCount
                                    items {
                                        timestamp
                                    }
                                }
                            }
                        }
                    }
                }
            }
            """;

        var result = await fixture.ExecuteGraphQlAsync(query);
        output.WriteLine(fixture.SerializeGraphQl(result));

        result.Errors.Should().BeNullOrEmpty();

        var rows = GetRowsData(result);
        var totalCount = rows.GetProperty("totalCount").GetInt32();
        // totalCount should reflect the capped limit
        totalCount.Should().BeLessThanOrEqualTo(7);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Navigates to the rows sub-connection of the first descriptor in the transient simple connection.
    /// Path: data → streamData → transientStreamDataQuery → simple → items[0] → rows
    /// </summary>
    private JsonElement GetRowsData(global::GraphQL.ExecutionResult result)
    {
        var json = fixture.SerializeGraphQl(result);
        var doc = JsonDocument.Parse(json);
        return doc.RootElement
            .GetProperty("data")
            .GetProperty("streamData")
            .GetProperty("transientStreamDataQuery")
            .GetProperty("simple")
            .GetProperty("items").EnumerateArray().First()
            .GetProperty("rows");
    }
}
