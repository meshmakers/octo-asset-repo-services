using System.Text.Json;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
/// Integration tests for the Between field-filter operator against CrateDB-backed stream data.
/// Guards Phase 1 of the stream/rt query symmetry refactor: proves the shared FieldFilter
/// flows GraphQL → engine mapper → CrateDbStreamDataRepository → CrateDB SQL BETWEEN.
/// </summary>
[Collection("Sequential")]
public class StreamDataBetweenFilterTests(StreamDataFixture fixture, ITestOutputHelper output)
    : IClassFixture<StreamDataFixture>
{
    [Fact]
    public async Task TransientStreamDataQuery_WithBetweenTimestampFilter_ReturnsExpectedRange()
    {
        fixture.OutputHelper = output;

        // TestDataStartTime is 2026-01-01T10:00:00Z, 20 points at 3-minute intervals.
        // Between start and start+15 inclusive covers points at 10:00, 10:03, 10:06, 10:09, 10:12, 10:15 = 6 rows.
        var from = fixture.TestDataStartTime.ToString("O");
        var to = fixture.TestDataStartTime.AddMinutes(15).ToString("O");

        var query = $$"""
            {
                streamData {
                    transientStreamDataQuery {
                        simple(
                            ckId: "{{fixture.TestCkTypeId}}"
                            columnPaths: ["Voltage"]
                            fieldFilter: [{
                                attributePath: "Timestamp"
                                operator: BETWEEN
                                comparisonValue: "{{from}}"
                                secondaryValue: "{{to}}"
                            }]
                            first: 1
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

        result.Errors.Should().BeNullOrEmpty("GraphQL query should succeed without errors");

        var rows = GetRowsData(result);
        var totalCount = rows.GetProperty("totalCount").GetInt32();
        totalCount.Should().Be(6,
            "Between 10:00 and 10:15 inclusive includes the points at 10:00, 10:03, 10:06, 10:09, 10:12, 10:15");

        var items = rows.GetProperty("items").EnumerateArray().ToList();
        items.Should().HaveCount(6);
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
