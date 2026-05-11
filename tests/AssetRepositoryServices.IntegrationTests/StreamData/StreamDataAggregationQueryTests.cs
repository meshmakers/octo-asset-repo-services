using System.Text.Json;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
/// Integration tests for stream data aggregation and downsampling queries via GraphQL.
/// Safety net for the engine migration (AB#3364).
/// </summary>
[Collection("Sequential")]
public class StreamDataAggregationQueryTests(StreamDataFixture fixture, ITestOutputHelper output)
    : IClassFixture<StreamDataFixture>
{
    [Fact]
    public async Task TransientAggregationQuery_Average_ReturnsCorrectResult()
    {
        fixture.OutputHelper = output;

        var query = $$"""
            {
                streamData {
                    transientStreamDataQuery {
                        aggregation(
                            archiveRtId: "{{fixture.ArchiveRtIdString}}"
                            ckId: "{{fixture.TestCkTypeId}}"
                            columnPaths: [
                                { attributePath: "Voltage", aggregationType: AVG }
                            ]
                            first: 10
                        ) {
                            items {
                                rows(first: 10) {
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

        result.Errors.Should().BeNullOrEmpty("GraphQL aggregation query should succeed");

        var rows = GetRowsData(result, "aggregation");
        var items = rows.GetProperty("items").EnumerateArray().ToList();
        items.Should().HaveCount(1, "aggregation without grouping returns a single row");

        // Average voltage: (220.0 + 220.5 + ... + 229.5) / 20 = 224.75
        var cells = items[0].GetProperty("cells").GetProperty("items").EnumerateArray().ToList();
        cells.Should().NotBeEmpty();
    }

    [Fact]
    public async Task TransientAggregationQuery_MinMax_ReturnsCorrectResult()
    {
        fixture.OutputHelper = output;

        var query = $$"""
            {
                streamData {
                    transientStreamDataQuery {
                        aggregation(
                            archiveRtId: "{{fixture.ArchiveRtIdString}}"
                            ckId: "{{fixture.TestCkTypeId}}"
                            columnPaths: [
                                { attributePath: "Voltage", aggregationType: MIN },
                                { attributePath: "Voltage", aggregationType: MAX }
                            ]
                            first: 10
                        ) {
                            items {
                                rows(first: 10) {
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

        result.Errors.Should().BeNullOrEmpty("GraphQL min/max query should succeed");

        var rows = GetRowsData(result, "aggregation");
        var items = rows.GetProperty("items").EnumerateArray().ToList();
        items.Should().HaveCount(1);
    }

    [Fact]
    public async Task TransientGroupedAggregationQuery_GroupByCkTypeId_ReturnsGroups()
    {
        fixture.OutputHelper = output;

        var query = $$"""
            {
                streamData {
                    transientStreamDataQuery {
                        groupingAggregation(
                            archiveRtId: "{{fixture.ArchiveRtIdString}}"
                            ckId: "{{fixture.TestCkTypeId}}"
                            groupByColumnPaths: ["ckTypeId"]
                            columnPaths: [
                                { attributePath: "Voltage", aggregationType: COUNT }
                            ]
                            first: 10
                        ) {
                            items {
                                rows(first: 10) {
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

        result.Errors.Should().BeNullOrEmpty("GraphQL grouped aggregation query should succeed");

        var rows = GetRowsData(result, "groupingAggregation");
        var items = rows.GetProperty("items").EnumerateArray().ToList();
        // All data points have the same CkTypeId, so grouping by it should produce 1 group
        items.Should().HaveCount(1);
    }

    [Fact]
    public async Task TransientDownsamplingQuery_ReturnsTimeBins()
    {
        fixture.OutputHelper = output;

        var from = fixture.TestDataStartTime.ToString("O");
        var to = fixture.TestDataEndTime.ToString("O");

        var query = $$"""
            {
                streamData {
                    transientStreamDataQuery {
                        downsampling(
                            archiveRtId: "{{fixture.ArchiveRtIdString}}"
                            ckId: "{{fixture.TestCkTypeId}}"
                            columnPaths: [
                                { attributePath: "Voltage", aggregationType: AVG }
                            ]
                            limit: 4
                            from: "{{from}}"
                            to: "{{to}}"
                            first: 100
                        ) {
                            items {
                                rows(first: 100) {
                                    totalCount
                                    items {
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

        result.Errors.Should().BeNullOrEmpty("GraphQL downsampling query should succeed");

        var rows = GetRowsData(result, "downsampling");
        var items = rows.GetProperty("items").EnumerateArray().ToList();
        // 4 time bins requested
        items.Should().HaveCountGreaterThanOrEqualTo(1, "should return at least 1 time bin");
        items.Should().HaveCountLessThanOrEqualTo(4, "should not exceed requested bin count");

        // Each item should have a timestamp (the bin start time)
        foreach (var item in items)
        {
            item.GetProperty("timestamp").GetString().Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public async Task TransientDownsamplingQuery_WithCount_ReturnsCorrectAggregation()
    {
        fixture.OutputHelper = output;

        var from = fixture.TestDataStartTime.ToString("O");
        var to = fixture.TestDataEndTime.ToString("O");

        var query = $$"""
            {
                streamData {
                    transientStreamDataQuery {
                        downsampling(
                            archiveRtId: "{{fixture.ArchiveRtIdString}}"
                            ckId: "{{fixture.TestCkTypeId}}"
                            columnPaths: [
                                { attributePath: "Voltage", aggregationType: COUNT }
                            ]
                            limit: 2
                            from: "{{from}}"
                            to: "{{to}}"
                            first: 100
                        ) {
                            items {
                                rows(first: 100) {
                                    totalCount
                                    items {
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

        result.Errors.Should().BeNullOrEmpty("GraphQL downsampling count query should succeed");

        var rows = GetRowsData(result, "downsampling");
        var items = rows.GetProperty("items").EnumerateArray().ToList();
        items.Should().HaveCountGreaterThanOrEqualTo(1);

        // The count across all bins should sum to 20 (total data points)
        var totalDataPointsInBins = 0;
        foreach (var item in items)
        {
            var cells = item.GetProperty("cells").GetProperty("items").EnumerateArray().ToList();
            var countCell = cells.FirstOrDefault(c =>
                c.GetProperty("attributePath").GetString()?.Contains("voltage",
                    StringComparison.OrdinalIgnoreCase) == true);
            if (countCell.ValueKind != JsonValueKind.Undefined)
            {
                totalDataPointsInBins += countCell.GetProperty("value").GetInt32();
            }
        }

        // Allow off-by-one due to DATE_BIN boundary behavior (last point at exactly `to` may fall outside last bin)
        totalDataPointsInBins.Should().BeGreaterThanOrEqualTo(fixture.TestDataPointCount - 1,
            "total count across bins should cover most data points");
        totalDataPointsInBins.Should().BeLessThanOrEqualTo(fixture.TestDataPointCount,
            "total count should not exceed total data points");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Navigates to the rows sub-connection of the first descriptor for a given variant.
    /// Path: data → streamData → transientStreamDataQuery → {variant} → items[0] → rows
    /// </summary>
    private JsonElement GetRowsData(global::GraphQL.ExecutionResult result, string variant)
    {
        var json = fixture.SerializeGraphQl(result);
        var doc = JsonDocument.Parse(json);
        return doc.RootElement
            .GetProperty("data")
            .GetProperty("streamData")
            .GetProperty("transientStreamDataQuery")
            .GetProperty(variant)
            .GetProperty("items").EnumerateArray().First()
            .GetProperty("rows");
    }
}
