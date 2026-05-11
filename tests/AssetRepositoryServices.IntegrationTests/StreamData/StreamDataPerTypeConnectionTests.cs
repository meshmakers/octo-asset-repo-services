using System.Text.Json;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
/// Integration tests for the per-type stream-data connection
/// (assetRepositoryIntegrationTestMeteringPoint on StreamDataModelQuery), which exposes typed
/// attribute fields directly on the row (not via cells). After T17 every column is camelCase
/// across the wire and the underlying CrateDB table — both the row keys and the GraphQL aliases.
/// </summary>
[Collection("Sequential")]
public class StreamDataPerTypeConnectionTests(StreamDataFixture fixture, ITestOutputHelper output)
    : IClassFixture<StreamDataFixture>
{
    [Fact]
    public async Task PerTypeConnection_ReturnsTypedAttributeValues()
    {
        fixture.OutputHelper = output;

        // Per-type connection naming: stream + camelCase CK type (model + type, slashes stripped).
        // AssetRepositoryIntegrationTest/MeteringPoint → assetRepositoryIntegrationTestMeteringPoint.
        // Per-archive table requires `archiveRtId`.
        var query = $$"""
            {
                streamData {
                    assetRepositoryIntegrationTestMeteringPoint(
                        archiveRtId: "{{fixture.ArchiveRtIdString}}"
                        first: 5
                    ) {
                        totalCount
                        items {
                            rtId
                            timestamp
                            rtWellKnownName
                            voltage
                            current
                        }
                    }
                }
            }
            """;

        var result = await fixture.ExecuteGraphQlAsync(query);
        output.WriteLine(fixture.SerializeGraphQl(result));

        result.Errors.Should().BeNullOrEmpty("GraphQL query should succeed without errors");

        var connection = GetConnection(result);
        connection.GetProperty("totalCount").GetInt32().Should()
            .Be(fixture.TestDataPointCount);

        var items = connection.GetProperty("items").EnumerateArray().ToList();
        items.Should().HaveCount(5);

        foreach (var item in items)
        {
            item.GetProperty("rtId").ValueKind.Should().NotBe(JsonValueKind.Null,
                "rtId is always populated for typed stream rows");
            item.GetProperty("timestamp").ValueKind.Should().NotBe(JsonValueKind.Null,
                "timestamp is always populated");
            item.GetProperty("rtWellKnownName").ValueKind.Should().NotBe(JsonValueKind.Null,
                "fixture populates rtWellKnownName as 'TestMeteringPointNNN'");

            item.GetProperty("voltage").ValueKind.Should().NotBe(JsonValueKind.Null,
                "typed attribute fields must resolve — regression check for the "
                + "camelCase row.Values vs PascalCase GetAttributeValueOrDefault mismatch");
            item.GetProperty("current").ValueKind.Should().NotBe(JsonValueKind.Null);
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
    /// Path: data → streamData → assetRepositoryIntegrationTestMeteringPoint
    /// </summary>
    private JsonElement GetConnection(global::GraphQL.ExecutionResult result)
    {
        var json = fixture.SerializeGraphQl(result);
        var doc = JsonDocument.Parse(json);
        return doc.RootElement
            .GetProperty("data")
            .GetProperty("streamData")
            .GetProperty("assetRepositoryIntegrationTestMeteringPoint");
    }
}
