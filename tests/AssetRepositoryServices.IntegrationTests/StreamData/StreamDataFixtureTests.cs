using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

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

        // The fixture already inserted 20 data points during initialization.
        // Verify we can retrieve all of them via the client.
        var count = await client.GetCountAsync(tenantId,
            $"""SELECT COUNT(*) FROM {tenantId}""");

        Assert.Equal(fixture.TestDataPointCount, count);

        // Retrieve all rows and verify structure
        var rows = await client.GetDataAsync(tenantId,
            $"""
            SELECT "RtId", "CkTypeId", "Timestamp", "RtWellKnownName", "data"['Voltage'] as "Voltage", "data"['Current'] as "Current"
            FROM {tenantId}
            ORDER BY "Timestamp" ASC
            """);

        Assert.Equal(fixture.TestDataPointCount, rows.Count);

        // Verify first and last data points match expected values
        var first = rows[0];
        Assert.Equal(fixture.TestDataStartTime, first.Timestamp);
        Assert.Equal(220.0, Convert.ToDouble(first.Attributes["Voltage"]), 0.01);
        Assert.Equal(10.0, Convert.ToDouble(first.Attributes["Current"]), 0.01);

        var last = rows[^1];
        Assert.Equal(fixture.TestDataEndTime, last.Timestamp);
        Assert.Equal(229.5, Convert.ToDouble(last.Attributes["Voltage"]), 1.0);
        Assert.Equal(11.9, Convert.ToDouble(last.Attributes["Current"]), 1.0);
    }
}
