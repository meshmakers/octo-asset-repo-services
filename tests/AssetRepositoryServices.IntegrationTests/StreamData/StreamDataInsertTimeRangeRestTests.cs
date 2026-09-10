using System.Text.Json;
using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Controllers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
/// AB#5185 against a real CrateDB: a time-range insert body with numeric, string and boolean attribute
/// values, bound exactly as ASP.NET Core binds it (System.Text.Json web defaults), lands in the archive
/// table with the columns' native types — integers as integers, not widened to floating point — and
/// reads back typed through the engine. Before the fix every such body failed with 500
/// (<c>InvalidCastException: Writing values of 'System.Text.Json.JsonElement' is not supported</c>).
/// Runs in a child tenant so the fixture's shared archives stay untouched.
/// </summary>
[Collection(StreamDataLifecycleCollection.Name)]
public class StreamDataInsertTimeRangeRestTests(StreamDataFixture fixture, ITestOutputHelper output)
{
    /// <summary>The options MVC binds <c>[FromBody]</c> with when the service registers none of its own.</summary>
    private static readonly JsonSerializerOptions MvcBodyOptions = new JsonOptions().JsonSerializerOptions;

    private static readonly DateTime WindowStart = new(2026, 8, 31, 22, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowEnd = new(2026, 9, 1, 22, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task InsertTimeRange_StoresJsonAttributeValuesWithTheColumnTypes()
    {
        fixture.OutputHelper = output;
        const string childTenantId = "ab5185rest";
        await CreateChildAsync(childTenantId);

        try
        {
            var child = await PrepareChildAsync(childTenantId);
            var archiveRtId = await CreateAndActivateTimeRangeArchiveAsync(child);
            var rtId = OctoObjectId.GenerateNewId();
            var body = $$"""
                [{
                  "rtId": "{{rtId}}",
                  "ckTypeId": "{{fixture.TestCkTypeId}}",
                  "from": "{{WindowStart:O}}",
                  "to": "{{WindowEnd:O}}",
                  "rtWellKnownName": "meter-1",
                  "attributes": {
                    "MeterReading": 4711,
                    "Voltage": 230.5,
                    "MeteringPointNumber": "AT0010000000000000001000004711",
                    "IsEstimated": true
                  }
                }]
                """;
            var points = JsonSerializer.Deserialize<IReadOnlyList<InsertTimeRangePointRestDto>>(body, MvcBodyOptions)
                ?? throw new InvalidOperationException("The body deserialized to null.");
            var controller = new StreamDataController(
                A.Fake<ILogger<StreamDataController>>(), fixture.GetSystemContext(), A.Fake<IHostApplicationLifetime>());

            var result = await controller.InsertTimeRange(childTenantId, archiveRtId.ToString(), points);

            result.Should().BeOfType<NoContentResult>();

            // Storage typing: what CrateDB holds per column, read through the driver's native mapping.
            var stored = await ReadStoredRowAsync(childTenantId, archiveRtId, rtId);
            stored["meterreading"].Should().BeOfType<int>().Which.Should().Be(4711);
            stored["voltage"].Should().BeOfType<double>().Which.Should().Be(230.5);
            stored["meteringpointnumber"].Should().Be("AT0010000000000000001000004711");
            stored["isestimated"].Should().Be(true);

            // Queryable through the engine's read path with the same types.
            var repository = child.GetStreamDataRepository()
                ?? throw new InvalidOperationException("StreamDataRepository not available.");
            var exported = await ExportAllAsync(repository, archiveRtId);
            var row = exported.Should().ContainSingle().Subject;
            row["meterreading"].Should().BeOfType<int>().Which.Should().Be(4711);
            row["voltage"].Should().BeOfType<double>().Which.Should().Be(230.5);
            row["meteringpointnumber"].Should().Be("AT0010000000000000001000004711");
            row["isestimated"].Should().Be(true);
        }
        finally
        {
            await DropChildIfExistingAsync(childTenantId);
        }
    }

    /// <summary>
    /// AB#5157 validation finding 4: a batch whose <c>ckTypeId</c> is not the archive's target used
    /// to answer 204 with nothing written — the repository drops those rows by design and said so
    /// only at DEBUG level. The usual trigger is the VERSIONED id, which parses fine and simply
    /// never matches. The endpoint now refuses the batch and names both ids.
    /// </summary>
    [Fact]
    public async Task InsertTimeRange_VersionedCkTypeId_IsRefusedInsteadOfSilentlyDroppingEveryPoint()
    {
        fixture.OutputHelper = output;
        const string childTenantId = "ab5157restckid";
        await CreateChildAsync(childTenantId);

        try
        {
            var child = await PrepareChildAsync(childTenantId);
            var archiveRtId = await CreateAndActivateTimeRangeArchiveAsync(child);

            var parts = fixture.TestCkTypeId.Split('/');
            var versioned = $"{parts[0]}-1.0.0/{parts[1]}-1";
            var body = $$"""
                [{
                  "rtId": "{{OctoObjectId.GenerateNewId()}}",
                  "ckTypeId": "{{versioned}}",
                  "from": "{{WindowStart:O}}",
                  "to": "{{WindowEnd:O}}",
                  "attributes": { "MeterReading": 4711 }
                }]
                """;
            var points = JsonSerializer.Deserialize<IReadOnlyList<InsertTimeRangePointRestDto>>(body, MvcBodyOptions)
                ?? throw new InvalidOperationException("The body deserialized to null.");
            var controller = new StreamDataController(
                A.Fake<ILogger<StreamDataController>>(), fixture.GetSystemContext(), A.Fake<IHostApplicationLifetime>());

            var result = await controller.InsertTimeRange(childTenantId, archiveRtId.ToString(), points);

            var message = result.Should().BeOfType<BadRequestObjectResult>().Which.Value.Should().BeOfType<string>().Subject;
            message.Should().Contain(versioned, "the caller must see what it sent");
            message.Should().Contain(fixture.TestCkTypeId, "…next to what the archive actually captures");

            (await CountRowsAsync(childTenantId, archiveRtId)).Should().Be(0, "the batch was refused, not partially applied");
        }
        finally
        {
            await DropChildIfExistingAsync(childTenantId);
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Enables stream data on the child and imports the test CK model.</summary>
    private async Task<ITenantContext> PrepareChildAsync(string childTenantId)
    {
        var child = await GetChildAsync(childTenantId);
        await child.EnableStreamDataAsync();
        var import = new OperationResult();
        await child.ImportCkModelAsync(new CkModelId("AssetRepositoryIntegrationTest"), import);
        import.HasErrors.Should().BeFalse(string.Join(", ", import.Messages.Select(m => m.MessageText)));
        return child;
    }

    /// <summary>
    /// One column per CK value type the acceptance criteria name: Int, Double, String and Boolean
    /// attributes of the test MeteringPoint type, provisioned as a windowed CrateDB table.
    /// </summary>
    private async Task<OctoObjectId> CreateAndActivateTimeRangeArchiveAsync(ITenantContext child)
    {
        var store = child.GetTimeRangeArchiveRuntimeStore()
            ?? throw new InvalidOperationException("TimeRangeArchiveRuntimeStore not available.");
        var archiveRtId = await store.InsertAsync(
            "RestInsertTypedArchive",
            new RtCkId<CkTypeId>(fixture.TestCkTypeId),
            new List<CkArchiveColumnSpec>
            {
                new("MeterReading", Indexed: true, Required: false),
                new("Voltage", Indexed: true, Required: false),
                new("MeteringPointNumber", Indexed: true, Required: false),
                new("IsEstimated", Indexed: true, Required: false),
            },
            TimeSpan.FromDays(1));

        var lifecycle = child.GetArchiveLifecycleService()
            ?? throw new InvalidOperationException("ArchiveLifecycleService not registered.");
        await lifecycle.ActivateAsync(archiveRtId);
        return archiveRtId;
    }

    /// <summary>
    /// Refreshes the archive table (CrateDB is eventually consistent) and reads the posted window
    /// back as physical column → driver-typed value.
    /// </summary>
    private async Task<Dictionary<string, object?>> ReadStoredRowAsync(
        string schema, OctoObjectId archiveRtId, OctoObjectId rtId)
    {
        var table = $"\"{schema}\".\"archive_{archiveRtId}\"";
        await using var connection = new NpgsqlConnection(fixture.CrateDbConnectionString);
        await connection.OpenAsync();
        await using (var refresh = new NpgsqlCommand($"REFRESH TABLE {table}", connection))
        {
            await refresh.ExecuteNonQueryAsync();
        }

        await using var select = new NpgsqlCommand(
            $"SELECT \"meterreading\", \"voltage\", \"meteringpointnumber\", \"isestimated\" FROM {table} WHERE \"rtid\" = @rtid",
            connection);
        select.Parameters.AddWithValue("rtid", rtId.ToString());
        await using var reader = await select.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue("the posted window must have been inserted");
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        }

        (await reader.ReadAsync()).Should().BeFalse("exactly one window was posted");
        return row;
    }

    /// <summary>Row count of the archive table after a refresh — 0 when a batch was refused.</summary>
    private async Task<long> CountRowsAsync(string schema, OctoObjectId archiveRtId)
    {
        var table = $"\"{schema}\".\"archive_{archiveRtId}\"";
        await using var connection = new NpgsqlConnection(fixture.CrateDbConnectionString);
        await connection.OpenAsync();
        await using (var refresh = new NpgsqlCommand($"REFRESH TABLE {table}", connection))
        {
            await refresh.ExecuteNonQueryAsync();
        }

        await using var count = new NpgsqlCommand($"SELECT COUNT(*) FROM {table}", connection);
        return Convert.ToInt64(await count.ExecuteScalarAsync());
    }

    private static async Task<List<IReadOnlyDictionary<string, object?>>> ExportAllAsync(
        IStreamDataRepository repository, OctoObjectId archiveRtId)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        await foreach (var row in repository.ExportRowsAsync(archiveRtId, null, TestContext.Current.CancellationToken))
        {
            rows.Add(row);
        }

        return rows;
    }

    private async Task CreateChildAsync(string tenantId)
    {
        var systemContext = fixture.GetSystemContext();
        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();
        await systemContext.CreateChildTenantAsync(session, tenantId, tenantId);
        await session.CommitTransactionAsync();
    }

    private async Task<ITenantContext> GetChildAsync(string tenantId)
    {
        var systemContext = fixture.GetSystemContext();
        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();
        var child = await systemContext.GetChildTenantContextAsync(session, tenantId);
        await session.CommitTransactionAsync();
        return child;
    }

    private async Task DropChildIfExistingAsync(string tenantId)
    {
        var systemContext = fixture.GetSystemContext();
        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();
        if (await systemContext.IsChildTenantExistingAsync(session, tenantId))
        {
            await systemContext.DropChildTenantAsync(session, tenantId, dropStreamData: true);
        }

        await session.CommitTransactionAsync();
    }
}
