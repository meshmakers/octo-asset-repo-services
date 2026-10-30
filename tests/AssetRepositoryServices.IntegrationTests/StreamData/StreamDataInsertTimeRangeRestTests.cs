using System.Security.Claims;
using System.Text.Json;
using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Controllers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
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

            // AB#7598: the answer says what was stored.
            var writeResult = result.Should().BeOfType<OkObjectResult>().Subject.Value
                .Should().BeOfType<StreamDataWriteResultRestDto>().Subject;
            writeResult.Stored.Should().Be(1);
            writeResult.Skipped.Should().Be(0);

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

    /// <summary>
    /// A value that does not fit its column (text for an integer column, an unknown name for an enum column)
    /// used to reach CrateDB, which refused the statement, and the endpoint answered 500. It now answers 400
    /// naming the point, the column and the expected type, and writes nothing of the batch.
    /// </summary>
    [Fact]
    public async Task InsertTimeRange_AValueOfTheWrongType_Answers400NamingPointColumnAndType_AndWritesNothing()
    {
        fixture.OutputHelper = output;
        const string childTenantId = "restmismatch";
        await CreateChildAsync(childTenantId);

        try
        {
            var child = await PrepareChildAsync(childTenantId);
            var archiveRtId = await CreateAndActivateTimeRangeArchiveAsync(child);
            var body = $$"""
                [{
                  "rtId": "{{OctoObjectId.GenerateNewId()}}",
                  "ckTypeId": "{{fixture.TestCkTypeId}}",
                  "from": "{{WindowStart:O}}",
                  "to": "{{WindowEnd:O}}",
                  "attributes": { "MeterReading": 4711, "OperatingStatus": "OK" }
                },
                {
                  "rtId": "{{OctoObjectId.GenerateNewId()}}",
                  "ckTypeId": "{{fixture.TestCkTypeId}}",
                  "from": "{{WindowStart:O}}",
                  "to": "{{WindowEnd:O}}",
                  "attributes": { "MeterReading": "kWh", "OperatingStatus": "Running" }
                }]
                """;
            var points = JsonSerializer.Deserialize<IReadOnlyList<InsertTimeRangePointRestDto>>(body, MvcBodyOptions)
                ?? throw new InvalidOperationException("The body deserialized to null.");
            var controller = new StreamDataController(
                A.Fake<ILogger<StreamDataController>>(), fixture.GetSystemContext(), A.Fake<IHostApplicationLifetime>());

            var result = await controller.InsertTimeRange(childTenantId, archiveRtId.ToString(), points);

            var message = result.Should().BeOfType<BadRequestObjectResult>().Which.Value.Should().BeOfType<string>().Subject;
            message.Should().Contain("point 1: column 'MeterReading' expects an integer, got text 'kWh'");
            message.Should().Contain("point 1: column 'OperatingStatus' expects a value of enum");
            message.Should().Contain("OK (1)", "the caller learns the valid values");
            message.Should().NotContain("point 0", "the first point fits its columns");
            (await CountRowsAsync(childTenantId, archiveRtId)).Should().Be(0, "the batch was refused, not partially applied");
        }
        finally
        {
            await DropChildIfExistingAsync(childTenantId);
        }
    }

    /// <summary>
    /// An enum column takes the member name (case-insensitive) or the key, as the pipeline write path does;
    /// the name is stored as its key.
    /// </summary>
    [Fact]
    public async Task InsertTimeRange_AnEnumMemberName_IsStoredAsItsKey()
    {
        fixture.OutputHelper = output;
        const string childTenantId = "restenumname";
        await CreateChildAsync(childTenantId);

        try
        {
            var child = await PrepareChildAsync(childTenantId);
            var archiveRtId = await CreateAndActivateTimeRangeArchiveAsync(child);
            var byName = OctoObjectId.GenerateNewId();
            var byKey = OctoObjectId.GenerateNewId();
            var body = $$"""
                [{
                  "rtId": "{{byName}}",
                  "ckTypeId": "{{fixture.TestCkTypeId}}",
                  "from": "{{WindowStart:O}}",
                  "to": "{{WindowEnd:O}}",
                  "attributes": { "OperatingStatus": "maintenance" }
                },
                {
                  "rtId": "{{byKey}}",
                  "ckTypeId": "{{fixture.TestCkTypeId}}",
                  "from": "{{WindowStart:O}}",
                  "to": "{{WindowEnd:O}}",
                  "attributes": { "OperatingStatus": 1 }
                }]
                """;
            var points = JsonSerializer.Deserialize<IReadOnlyList<InsertTimeRangePointRestDto>>(body, MvcBodyOptions)
                ?? throw new InvalidOperationException("The body deserialized to null.");
            var controller = new StreamDataController(
                A.Fake<ILogger<StreamDataController>>(), fixture.GetSystemContext(), A.Fake<IHostApplicationLifetime>());

            var result = await controller.InsertTimeRange(childTenantId, archiveRtId.ToString(), points);

            result.Should().BeOfType<OkObjectResult>().Which.Value
                .Should().BeOfType<StreamDataWriteResultRestDto>().Which.Stored.Should().Be(2);
            (await ReadOperatingStatusAsync(childTenantId, archiveRtId, byName)).Should().Be(2);
            (await ReadOperatingStatusAsync(childTenantId, archiveRtId, byKey)).Should().Be(1);
        }
        finally
        {
            await DropChildIfExistingAsync(childTenantId);
        }
    }

    /// <summary>
    /// A manual correction is visible to the very next read: the write refreshes the table instead of leaving
    /// it to CrateDB's asynchronous refresh, so a person who saves and reloads does not see the old value.
    /// The read below deliberately issues no REFRESH of its own.
    /// </summary>
    [Fact]
    public async Task InsertTimeRange_AManualCorrection_ReadsBackImmediately()
    {
        fixture.OutputHelper = output;
        const string childTenantId = "restmanualread";
        await CreateChildAsync(childTenantId);

        try
        {
            var child = await PrepareChildAsync(childTenantId);
            var archiveRtId = await CreateAndActivateTimeRangeArchiveAsync(child);
            var rtId = OctoObjectId.GenerateNewId();
            var controller = ControllerFor(childTenantId, ("sub", "8a4c1f00-0000-4000-8000-000000000001"));

            foreach (var reading in new[] { 100, 200 })
            {
                var body = $$"""
                    [{
                      "rtId": "{{rtId}}",
                      "ckTypeId": "{{fixture.TestCkTypeId}}",
                      "from": "{{WindowStart:O}}",
                      "to": "{{WindowEnd:O}}",
                      "attributes": { "MeterReading": {{reading}} },
                      "origin": "manual",
                      "reason": "corrected"
                    }]
                    """;
                var points = JsonSerializer.Deserialize<IReadOnlyList<InsertTimeRangePointRestDto>>(body, MvcBodyOptions)
                    ?? throw new InvalidOperationException("The body deserialized to null.");

                var result = await controller.InsertTimeRange(childTenantId, archiveRtId.ToString(), points);

                result.Should().BeOfType<OkObjectResult>();
                (await ReadMeterReadingWithoutRefreshAsync(childTenantId, archiveRtId, rtId))
                    .Should().Be(reading, "the correction must be visible to the next read, without waiting for CrateDB");
            }
        }
        finally
        {
            await DropChildIfExistingAsync(childTenantId);
        }
    }

    /// <summary>
    /// AB#7656: a manual correction wins and stays protected by default — the archive here declares no conflict
    /// precedence at all. A manual write replaces a delivered value, a later automated delivery is answered as
    /// skipped with <c>ManualValueProtected</c> and leaves the correction in place, another manual write replaces it.
    /// </summary>
    [Fact]
    public async Task InsertTimeRange_WithoutPrecedence_AManualValueWinsAndStaysProtected()
    {
        fixture.OutputHelper = output;
        const string childTenantId = "restorigindefault";
        await CreateChildAsync(childTenantId);

        try
        {
            var child = await PrepareChildAsync(childTenantId);
            var archiveRtId = await CreateAndActivateTimeRangeArchiveAsync(child);
            var rtId = OctoObjectId.GenerateNewId();
            var controller = ControllerFor(childTenantId, ("sub", "8a4c1f00-0000-4000-8000-000000000002"));

            (await WriteReadingAsync(controller, childTenantId, archiveRtId, rtId, 100, manual: false)).Stored.Should().Be(1);
            (await WriteReadingAsync(controller, childTenantId, archiveRtId, rtId, 200, manual: true)).Stored.Should().Be(1);

            var automated = await WriteReadingAsync(controller, childTenantId, archiveRtId, rtId, 300, manual: false);
            automated.Stored.Should().Be(0);
            automated.SkippedByReason.Should().ContainKey(nameof(StreamDataSkipReason.ManualValueProtected))
                .WhoseValue.Should().Be(1);
            (await ReadMeterReadingAsync(childTenantId, archiveRtId, rtId)).Should().Be(200);

            (await WriteReadingAsync(controller, childTenantId, archiveRtId, rtId, 250, manual: true)).Stored.Should().Be(1);
            (await ReadMeterReadingAsync(childTenantId, archiveRtId, rtId)).Should().Be(250);
        }
        finally
        {
            await DropChildIfExistingAsync(childTenantId);
        }
    }

    /// <summary>
    /// AB#7656: an archive opts out with <c>$Origin</c> declared as <c>Ignore</c> — a manual value then has no
    /// priority, and without other keys the last write wins.
    /// </summary>
    [Fact]
    public async Task InsertTimeRange_WithTheOriginIgnored_AnAutomatedWriteReplacesAManualValue()
    {
        fixture.OutputHelper = output;
        const string childTenantId = "restoriginignore";
        await CreateChildAsync(childTenantId);

        try
        {
            var child = await PrepareChildAsync(childTenantId);
            var archiveRtId = await CreateAndActivateTimeRangeArchiveAsync(child, ignoreOrigin: true);
            var rtId = OctoObjectId.GenerateNewId();
            var controller = ControllerFor(childTenantId, ("sub", "8a4c1f00-0000-4000-8000-000000000003"));

            (await WriteReadingAsync(controller, childTenantId, archiveRtId, rtId, 200, manual: true)).Stored.Should().Be(1);
            var automated = await WriteReadingAsync(controller, childTenantId, archiveRtId, rtId, 300, manual: false);

            automated.Stored.Should().Be(1);
            automated.Skipped.Should().Be(0);
            (await ReadMeterReadingAsync(childTenantId, archiveRtId, rtId)).Should().Be(300);
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
    /// attributes of the test MeteringPoint type, plus the Enum attribute OperatingStatus (Unknown 0, OK 1,
    /// Maintenance 2), provisioned as a windowed CrateDB table.
    /// </summary>
    private async Task<OctoObjectId> CreateAndActivateTimeRangeArchiveAsync(ITenantContext child, bool ignoreOrigin = false)
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
                new("OperatingStatus", Indexed: true, Required: false),
            },
            TimeSpan.FromDays(1));

        if (ignoreOrigin)
        {
            // The opt-out of AB#7656: $Origin declared with Order Ignore.
            var repository = child.GetTenantRepository();
            using var session = await repository.GetSessionAsync();
            session.StartTransaction();
            var archive = await repository.GetRtEntityByRtIdAsync<RtTimeRangeArchive>(session, archiveRtId)
                ?? throw new InvalidOperationException("The archive was not stored.");
            archive.ConflictPrecedence = new AttributeRecordValueList<RtCkArchiveConflictKeyRecord>
            {
                new() { Column = ArchiveConflictKey.OriginKey, Order = RtCkConflictKeyOrderEnum.Ignore }
            };
            await repository.ReplaceOneRtEntityByIdAsync(session, archiveRtId, archive);
            await session.CommitTransactionAsync();
        }

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

    /// <summary>The stored OperatingStatus key of the posted window of <paramref name="rtId" />.</summary>
    private async Task<int?> ReadOperatingStatusAsync(string schema, OctoObjectId archiveRtId, OctoObjectId rtId)
    {
        var table = $"\"{schema}\".\"archive_{archiveRtId}\"";
        await using var connection = new NpgsqlConnection(fixture.CrateDbConnectionString);
        await connection.OpenAsync();
        await using (var refresh = new NpgsqlCommand($"REFRESH TABLE {table}", connection))
        {
            await refresh.ExecuteNonQueryAsync();
        }

        await using var select = new NpgsqlCommand($"SELECT \"operatingstatus\" FROM {table} WHERE \"rtid\" = @rtid", connection);
        select.Parameters.AddWithValue("rtid", rtId.ToString());
        var value = await select.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToInt32(value);
    }

    /// <summary>A controller whose caller carries the given token claims.</summary>
    private StreamDataController ControllerFor(string tenantId, params (string Type, string Value)[] claims)
    {
        var controller = new StreamDataController(
            A.Fake<ILogger<StreamDataController>>(), fixture.GetSystemContext(), A.Fake<IHostApplicationLifetime>());
        var identity = new ClaimsIdentity(
            claims.Select(c => new Claim(c.Type, c.Value)), "Bearer");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    /// <summary>Posts one window of <paramref name="rtId" /> with the given reading and returns the write result.</summary>
    private async Task<StreamDataWriteResultRestDto> WriteReadingAsync(StreamDataController controller, string tenantId,
        OctoObjectId archiveRtId, OctoObjectId rtId, int reading, bool manual)
    {
        var origin = manual ? "\"origin\": \"manual\"," : "";
        var body = $$"""
            [{
              {{origin}}
              "rtId": "{{rtId}}",
              "ckTypeId": "{{fixture.TestCkTypeId}}",
              "from": "{{WindowStart:O}}",
              "to": "{{WindowEnd:O}}",
              "attributes": { "MeterReading": {{reading}} }
            }]
            """;
        var points = JsonSerializer.Deserialize<IReadOnlyList<InsertTimeRangePointRestDto>>(body, MvcBodyOptions)
            ?? throw new InvalidOperationException("The body deserialized to null.");

        var result = await controller.InsertTimeRange(tenantId, archiveRtId.ToString(), points);

        return result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<StreamDataWriteResultRestDto>().Subject;
    }

    /// <summary>The stored MeterReading of the window of <paramref name="rtId" />, after a table refresh.</summary>
    private async Task<int?> ReadMeterReadingAsync(string schema, OctoObjectId archiveRtId, OctoObjectId rtId)
    {
        await CountRowsAsync(schema, archiveRtId);   // refreshes the table
        return await ReadMeterReadingWithoutRefreshAsync(schema, archiveRtId, rtId);
    }

    /// <summary>The stored MeterReading of the window of <paramref name="rtId" />, read WITHOUT a table refresh.</summary>
    private async Task<int?> ReadMeterReadingWithoutRefreshAsync(string schema, OctoObjectId archiveRtId, OctoObjectId rtId)
    {
        var table = $"\"{schema}\".\"archive_{archiveRtId}\"";
        await using var connection = new NpgsqlConnection(fixture.CrateDbConnectionString);
        await connection.OpenAsync();
        await using var select = new NpgsqlCommand($"SELECT \"meterreading\" FROM {table} WHERE \"rtid\" = @rtid", connection);
        select.Parameters.AddWithValue("rtid", rtId.ToString());
        var value = await select.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToInt32(value);
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
