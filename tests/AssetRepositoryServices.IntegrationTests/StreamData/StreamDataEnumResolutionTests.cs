using System.Text.Json;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
/// Integration tests for enum-name resolution on stream-data query results (AB#4247).
/// Enum-typed columns (here <c>OperatingStatus</c>: 0=Unknown, 1=OK, 2=Maintenance) must surface
/// their value NAME on the wire — parity with the runtime query path — instead of the raw integer
/// key stored in CrateDB. Covered across all transient query shapes (simple / aggregation /
/// groupingAggregation / downsampling), which share the same column-mapping + cell-resolution code
/// as the persisted entry point. The <c>resolveEnumValuesToNames: false</c> toggle returns the raw
/// integer for API back-compat.
/// </summary>
[Collection("Sequential")]
public class StreamDataEnumResolutionTests(StreamDataFixture fixture, ITestOutputHelper output)
    : IClassFixture<StreamDataFixture>
{
    private static readonly string[] EnumNames = ["Unknown", "OK", "Maintenance"];

    [Fact]
    public async Task TransientSimpleQuery_EnumColumn_ResolvesIntKeyToName()
    {
        fixture.OutputHelper = output;

        var query = $$"""
            {
                streamData {
                    transientStreamDataQuery {
                        simple(
                            archiveRtId: "{{fixture.ArchiveRtIdString}}"
                            columnPaths: ["OperatingStatus"]
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

        var rows = GetRowsData(result, "simple");
        var items = rows.GetProperty("items").EnumerateArray().ToList();
        items.Should().HaveCount(fixture.TestDataPointCount);

        var values = items
            .Select(i => GetCell(i, "OperatingStatus"))
            .ToList();

        values.Should().OnlyContain(v => v.ValueKind == JsonValueKind.String,
            "enum cells must surface as their resolved name string, not the raw integer key");
        values.Select(v => v.GetString())
            .Should().OnlyContain(s => EnumNames.Contains(s));
        // The seeded spread (i % 3) guarantees all three names appear.
        values.Select(v => v.GetString()).Distinct()
            .Should().BeEquivalentTo(EnumNames);
    }

    [Fact]
    public async Task TransientSimpleQuery_EnumColumn_ResolveDisabled_ReturnsRawIntKey()
    {
        fixture.OutputHelper = output;

        var query = $$"""
            {
                streamData {
                    transientStreamDataQuery {
                        simple(
                            archiveRtId: "{{fixture.ArchiveRtIdString}}"
                            columnPaths: ["OperatingStatus"]
                            first: 100
                        ) {
                            items {
                                rows(first: 100) {
                                    items {
                                        cells(first: 10, resolveEnumValuesToNames: false) {
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

        var rows = GetRowsData(result, "simple");
        var items = rows.GetProperty("items").EnumerateArray().ToList();

        var values = items.Select(i => GetCell(i, "OperatingStatus")).ToList();
        values.Should().OnlyContain(v => v.ValueKind == JsonValueKind.Number,
            "with resolveEnumValuesToNames=false the raw integer key must pass through unchanged");
        values.Select(v => v.GetInt32()).Should().OnlyContain(k => k >= 0 && k <= 2);
    }

    [Fact]
    public async Task TransientAggregationQuery_EnumMax_ResolvesToName_CountStaysNumeric()
    {
        fixture.OutputHelper = output;

        // MAX of an enum returns one of the source integer keys → resolve to its name.
        // COUNT produces a derived number that is NOT an enum key → must stay numeric.
        var query = $$"""
            {
                streamData {
                    transientStreamDataQuery {
                        aggregation(
                            archiveRtId: "{{fixture.ArchiveRtIdString}}"
                            columnPaths: [
                                { attributePath: "OperatingStatus", aggregationType: MAX },
                                { attributePath: "OperatingStatus", aggregationType: COUNT }
                            ]
                            first: 10
                        ) {
                            items {
                                rows(first: 10) {
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

        var rows = GetRowsData(result, "aggregation");
        var items = rows.GetProperty("items").EnumerateArray().ToList();
        items.Should().HaveCount(1);

        var cells = items[0].GetProperty("cells").GetProperty("items").EnumerateArray().ToList();

        var maxCell = cells.First(c => PathContains(c, "max")).GetProperty("value");
        maxCell.ValueKind.Should().Be(JsonValueKind.String);
        maxCell.GetString().Should().Be("Maintenance", "max key is 2 → Maintenance");

        var countCell = cells.First(c => PathContains(c, "count")).GetProperty("value");
        countCell.ValueKind.Should().Be(JsonValueKind.Number,
            "COUNT is a derived number, not an enum key — it must not be enum-resolved");
        countCell.GetInt32().Should().Be(fixture.TestDataPointCount);
    }

    [Fact]
    public async Task TransientGroupingAggregation_GroupByEnum_ResolvesGroupKeyToName()
    {
        fixture.OutputHelper = output;

        var query = $$"""
            {
                streamData {
                    transientStreamDataQuery {
                        groupingAggregation(
                            archiveRtId: "{{fixture.ArchiveRtIdString}}"
                            groupByColumnPaths: ["OperatingStatus"]
                            columnPaths: [
                                { attributePath: "Voltage", aggregationType: COUNT }
                            ]
                            first: 10
                        ) {
                            items {
                                rows(first: 10) {
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

        var rows = GetRowsData(result, "groupingAggregation");
        var items = rows.GetProperty("items").EnumerateArray().ToList();
        // Three distinct OperatingStatus keys were seeded → three groups.
        items.Should().HaveCount(3);

        var groupKeys = items
            .Select(i => GetCell(i, "OperatingStatus"))
            .ToList();
        groupKeys.Should().OnlyContain(v => v.ValueKind == JsonValueKind.String,
            "grouping group-by keys on an enum column must resolve to the enum name");
        groupKeys.Select(v => v.GetString()).Should().BeEquivalentTo(EnumNames);
    }

    [Fact]
    public async Task TransientDownsamplingQuery_EnumMax_ResolvesToName()
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
                            columnPaths: [
                                { attributePath: "OperatingStatus", aggregationType: MAX }
                            ]
                            limit: 2
                            from: "{{from}}"
                            to: "{{to}}"
                            first: 100
                        ) {
                            items {
                                rows(first: 100) {
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

        result.Errors.Should().BeNullOrEmpty();

        var rows = GetRowsData(result, "downsampling");
        var items = rows.GetProperty("items").EnumerateArray().ToList();
        items.Should().HaveCountGreaterThanOrEqualTo(1);

        // Every bin's MAX(OperatingStatus) cell must be a resolved enum name, never a raw integer.
        var maxValues = items
            .Select(i => i.GetProperty("cells").GetProperty("items").EnumerateArray()
                .First(c => PathContains(c, "operatingstatus")).GetProperty("value"))
            .ToList();

        maxValues.Should().OnlyContain(v => v.ValueKind == JsonValueKind.String,
            "downsampling MAX of an enum column must resolve to the enum name");
        maxValues.Select(v => v.GetString()).Should().OnlyContain(s => EnumNames.Contains(s));
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static bool PathContains(JsonElement cell, string fragment) =>
        cell.GetProperty("attributePath").GetString()?
            .Contains(fragment, StringComparison.OrdinalIgnoreCase) == true;

    private static JsonElement GetCell(JsonElement rowItem, string attributePath)
    {
        var cells = rowItem.GetProperty("cells").GetProperty("items").EnumerateArray();
        return cells.First(c => c.GetProperty("attributePath").GetString() == attributePath)
            .GetProperty("value");
    }

    /// <summary>
    /// Navigates to the rows sub-connection of the first descriptor for a given transient variant.
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
