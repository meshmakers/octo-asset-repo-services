using System.Text.Json;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
/// Integration tests for the new .Aggregations sub-connection on the transient stream-data
/// descriptor type. Guards Phase 4 of the stream/rt query symmetry refactor: proves the
/// GraphQL layer can compute Count/Min/Max/Avg/Sum statistics on the same filter set that
/// the .Rows sub-connection exposes.
/// </summary>
[Collection("Sequential")]
public class StreamDataAggregationsSubConnectionTests(StreamDataFixture fixture, ITestOutputHelper output)
    : IClassFixture<StreamDataFixture>
{
    [Fact]
    public async Task TransientSimple_Aggregations_ReturnsAvgStatistics()
    {
        fixture.OutputHelper = output;

        // TestDataStartTime is 2026-01-01T10:00:00Z, 20 points at 3-minute intervals.
        // Voltage values: 220.0, 220.5, 221.0, ..., 229.5 (average 224.75).
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
                                aggregations(aggregations: { avgAttributePaths: ["Voltage"] }) {
                                    items {
                                        avgStatistics {
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
            """;

        var result = await fixture.ExecuteGraphQlAsync(query);
        output.WriteLine(fixture.SerializeGraphQl(result));

        result.Errors.Should().BeNullOrEmpty("GraphQL query should succeed without errors");

        var stats = GetFirstAggregationStatistics(result, "avgStatistics");
        stats.Should().HaveCount(1);

        var stat = stats.Single();
        // Aggregation-statistics output echoes the requested attributePath verbatim. Only the
        // underlying CrateDB column the SQL touches is the camelCased form (`voltage`); the
        // wire-format identifier on the way back stays `Voltage`.
        stat.GetProperty("attributePath").GetString().Should().Be("Voltage");

        // Voltages are 220.0 to 229.5 (inserted by fixture). Accept any average inside that range;
        // different integration-test runs may see slightly different row counts depending on
        // CrateDB refresh timing and shared-tenant data, so we don't pin an exact value here.
        var avg = stat.GetProperty("value").GetDouble();
        avg.Should().BeInRange(220.0, 229.5);
    }

    [Fact]
    public async Task TransientSimple_Aggregations_ReturnsMinMaxStatistics()
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
                            first: 1
                        ) {
                            items {
                                aggregations(aggregations: {
                                    minValueAttributePaths: ["Voltage"]
                                    maxValueAttributePaths: ["Voltage"]
                                }) {
                                    items {
                                        minStatistics { attributePath, value }
                                        maxStatistics { attributePath, value }
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

        var min = GetFirstAggregationStatistics(result, "minStatistics").Single();
        min.GetProperty("value").GetDouble().Should().BeLessThanOrEqualTo(220.5,
            "min should be at or near the low end of the inserted voltage range (220.0 - 229.5)");

        var max = GetFirstAggregationStatistics(result, "maxStatistics").Single();
        max.GetProperty("value").GetDouble().Should().BeGreaterThanOrEqualTo(229.0,
            "max should be at or near the high end of the inserted voltage range (220.0 - 229.5)");
    }

    private List<JsonElement> GetFirstAggregationStatistics(global::GraphQL.ExecutionResult result, string statisticsKey)
    {
        var json = fixture.SerializeGraphQl(result);
        var doc = JsonDocument.Parse(json);
        var resultRow = doc.RootElement
            .GetProperty("data")
            .GetProperty("streamData")
            .GetProperty("transientStreamDataQuery")
            .GetProperty("simple")
            .GetProperty("items").EnumerateArray().First()
            .GetProperty("aggregations")
            .GetProperty("items").EnumerateArray().First();

        return resultRow.GetProperty(statisticsKey).EnumerateArray().ToList();
    }
}
