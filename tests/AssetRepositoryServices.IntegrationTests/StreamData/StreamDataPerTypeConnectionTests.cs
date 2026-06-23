using System.Text.Json;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
/// Integration tests for resolving attribute values on stream-data rows. After AB#3864 the
/// per-CK-type connection on StreamDataModelQuery was removed; attribute values are now
/// projected through cells on the transient simple query (`streamData.transientStreamDataQuery.simple`).
/// This test still pins the row-keying regression by walking cell.attributePath/value pairs —
/// the wire echoes the caller's requested column string verbatim, while the engine-side
/// <c>StreamDataRow.Values</c> stays keyed by the lower-case CrateDB column name.
/// </summary>
[Collection("Sequential")]
public class StreamDataPerTypeConnectionTests(StreamDataFixture fixture, ITestOutputHelper output)
    : IClassFixture<StreamDataFixture>
{
    [Fact]
    public async Task TransientSimpleQuery_ReturnsTypedAttributeValues()
    {
        fixture.OutputHelper = output;

        var query = $$"""
            {
                streamData {
                    transientStreamDataQuery {
                        simple(
                            archiveRtId: "{{fixture.ArchiveRtIdString}}"
                            columnPaths: ["Voltage", "Current"]
                            first: 5
                        ) {
                            items {
                                rows(first: 5) {
                                    totalCount
                                    items {
                                        rtId
                                        timestamp
                                        rtWellKnownName
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

        var rows = GetRows(result);
        rows.GetProperty("totalCount").GetInt32().Should().Be(fixture.TestDataPointCount);

        var items = rows.GetProperty("items").EnumerateArray().ToList();
        items.Should().HaveCount(5);

        foreach (var item in items)
        {
            item.GetProperty("rtId").ValueKind.Should().NotBe(JsonValueKind.Null,
                "rtId is always populated for stream rows");
            item.GetProperty("timestamp").ValueKind.Should().NotBe(JsonValueKind.Null,
                "timestamp is always populated");
            item.GetProperty("rtWellKnownName").ValueKind.Should().NotBe(JsonValueKind.Null,
                "fixture populates rtWellKnownName as 'TestMeteringPointNNN'");

            var cells = item.GetProperty("cells").GetProperty("items").EnumerateArray().ToList();
            var byPath = cells.ToDictionary(
                c => c.GetProperty("attributePath").GetString()!,
                c => c.GetProperty("value"));

            byPath.Should().ContainKey("Voltage",
                "wire attributePath must echo the caller's requested column string verbatim "
                + "— if the engine's storage key leaks through, client grids bind to the "
                + "wrong field and cells render empty");
            byPath.Should().ContainKey("Current");
            byPath["Voltage"].ValueKind.Should().NotBe(JsonValueKind.Null);
            byPath["Current"].ValueKind.Should().NotBe(JsonValueKind.Null);
        }
    }

    [Fact]
    public async Task PerTypeConnection_RowValuesAreKeyedInCamelCase()
    {
        // T17 inverted the row-key casing convention: per-archive tables use camelCase columns
        // throughout the engine, so `StreamDataRow.Values` is keyed by camelCase too.
        fixture.OutputHelper = output;

        var rows = await fixture.ExecuteRepoQueryDirectAsync(
            fixture.TestCkTypeId,
            new[] { "Voltage", "Current" });

        rows.Should().NotBeEmpty();
        foreach (var row in rows)
        {
            foreach (var key in row.Values.Keys)
            {
                key.Should().MatchRegex("^[a-z][a-zA-Z0-9]*$",
                    "stream-data row keys must be camelCase after T17 (matches CrateDB column names)");
            }
        }
    }

    /// <summary>
    /// Path: data → streamData → transientStreamDataQuery → simple → items[0] → rows
    /// </summary>
    private JsonElement GetRows(global::GraphQL.ExecutionResult result)
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
