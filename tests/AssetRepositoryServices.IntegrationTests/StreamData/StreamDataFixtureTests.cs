using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

// Verifies the fixture's archive provisioning + insert path end-to-end against the per-archive
// CrateDB table introduced by T17 (no more `data` blob, no more legacy single-tenant table).
[Collection("Sequential")]
public class StreamDataFixtureTests(StreamDataFixture fixture, ITestOutputHelper output)
    : IClassFixture<StreamDataFixture>
{
    [Fact]
    public async Task InsertAndRetrieve_ReturnsAllDataPoints()
    {
        fixture.OutputHelper = output;
        var client = fixture.GetService<IStreamDataDatabaseClient>();
        var tenantId = fixture.StreamDataTenantId;
        var qualifiedTable = $"\"{tenantId}\".\"archive_{fixture.ArchiveRtIdString}\"";

        // The fixture seeds 20 points into the per-archive table during initialisation. The SELECT
        // uses direct lower-cased columns — the legacy `data['Voltage']` syntax is gone, and
        // CrateDB column names are lower-cased to sidestep case-preservation quirks.
        var count = await client.GetCountAsync(tenantId,
            $"SELECT COUNT(*) FROM {qualifiedTable}");

        Assert.Equal(fixture.TestDataPointCount, count);

        var rows = await client.GetDataAsync(tenantId,
            $"""
            SELECT "rtid", "cktypeid", "timestamp", "rtwellknownname", "voltage", "current"
            FROM {qualifiedTable}
            ORDER BY "timestamp" ASC
            """);

        Assert.Equal(fixture.TestDataPointCount, rows.Count);

        var first = rows[0];
        Assert.Equal(fixture.TestDataStartTime, first.Timestamp);
        Assert.Equal(220.0, Convert.ToDouble(first.Attributes["voltage"]), 0.01);
        Assert.Equal(10.0, Convert.ToDouble(first.Attributes["current"]), 0.01);

        var last = rows[^1];
        Assert.Equal(fixture.TestDataEndTime, last.Timestamp);
        Assert.Equal(229.5, Convert.ToDouble(last.Attributes["voltage"]), 1.0);
        Assert.Equal(11.9, Convert.ToDouble(last.Attributes["current"]), 1.0);
    }
}
