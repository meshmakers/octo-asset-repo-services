using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Controllers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
/// AB#8590 against a real CrateDB: <c>POST archives/{archiveRtId}/insert</c> writes raw rows through the engine's raw
/// <c>InsertAsync</c>. A manual correction wins over a delivered value and stays protected against automated
/// re-deliveries, per row; the column checks run against the real archive snapshot. Runs in a child tenant so the
/// fixture's shared archives stay untouched.
/// </summary>
[Collection(StreamDataLifecycleCollection.Name)]
public class StreamDataInsertRawRestTests(StreamDataFixture fixture, ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions MvcBodyOptions = new JsonOptions().JsonSerializerOptions;
    private static readonly DateTime Timestamp = new(2026, 3, 1, 10, 15, 0, 123, DateTimeKind.Utc);

    [Fact]
    public async Task Insert_AManualCorrectionWins_AndAnAutomatedRedeliveryLeavesTheWholeRowManual()
    {
        fixture.OutputHelper = output;
        const string childTenantId = "rawrestmanual";
        await CreateChildAsync(childTenantId);

        try
        {
            var archiveRtId = await PrepareRawArchiveAsync(childTenantId);
            var rtId = OctoObjectId.GenerateNewId();
            var controller = ControllerFor(("sub", "8a4c1f00-0000-4000-8000-000000000010"), ("preferred_username", "jane.doe"));

            (await WriteAsync(controller, childTenantId, archiveRtId, rtId, 100, 230.0, null)).Stored.Should().Be(1);
            (await WriteAsync(controller, childTenantId, archiveRtId, rtId, 200, 230.0, "meter exchanged")).Stored.Should().Be(1);

            // The automated re-delivery changes both columns; the row is manual, so neither changes (per-row protection).
            var automated = await WriteAsync(controller, childTenantId, archiveRtId, rtId, 300, 240.0, null);
            automated.Stored.Should().Be(0);
            automated.SkippedByReason.Should().ContainKey(nameof(StreamDataSkipReason.ManualValueProtected)).WhoseValue.Should().Be(1);
            var skipped = automated.SkippedRows.Should().ContainSingle().Subject;
            skipped.RtId.Should().Be(rtId.ToString());
            skipped.From.Should().Be(Timestamp);
            skipped.To.Should().BeNull();

            var row = await ReadRowAsync(childTenantId, archiveRtId, rtId);
            Convert.ToInt32(row["meterreading"]).Should().Be(200);
            Convert.ToDouble(row["voltage"]).Should().Be(230.0);
            row["row_origin"].Should().Be("manual");
            row["manual_written_by"].Should().Be("jane.doe");
            row["manual_reason"].Should().Be("meter exchanged");
        }
        finally
        {
            await DropChildIfExistingAsync(childTenantId);
        }
    }

    [Fact]
    public async Task Insert_AManualPointWithoutEveryColumn_Answers400_AndWritesNothing()
    {
        fixture.OutputHelper = output;
        const string childTenantId = "rawrestmissing";
        await CreateChildAsync(childTenantId);

        try
        {
            var archiveRtId = await PrepareRawArchiveAsync(childTenantId);
            var controller = ControllerFor(("sub", "user-1"));
            var points = Bind($$"""
                [{ "rtId": "{{OctoObjectId.GenerateNewId()}}", "ckTypeId": "{{fixture.TestCkTypeId}}",
                   "timestamp": "{{Timestamp:O}}", "attributes": { "MeterReading": 1 }, "origin": "manual" }]
                """);

            var result = await controller.Insert(childTenantId, archiveRtId.ToString(), points);

            result.Should().BeOfType<BadRequestObjectResult>().Which.Value.Should().BeOfType<string>()
                .Which.Should().Contain("Point 0").And.Contain("'Voltage'");
            (await CountRowsAsync(childTenantId, archiveRtId)).Should().Be(0);
        }
        finally
        {
            await DropChildIfExistingAsync(childTenantId);
        }
    }

    [Fact]
    public async Task Insert_AnAttributeThatIsNoColumnOfTheArchive_Answers400_AndWritesNothing()
    {
        fixture.OutputHelper = output;
        const string childTenantId = "rawrestunknown";
        await CreateChildAsync(childTenantId);

        try
        {
            var archiveRtId = await PrepareRawArchiveAsync(childTenantId);
            var controller = ControllerFor(("sub", "user-1"));
            var points = Bind($$"""
                [{ "rtId": "{{OctoObjectId.GenerateNewId()}}", "ckTypeId": "{{fixture.TestCkTypeId}}",
                   "timestamp": "{{Timestamp:O}}", "attributes": { "MeterReading": 1, "Current": 2.5 } }]
                """);

            var result = await controller.Insert(childTenantId, archiveRtId.ToString(), points);

            result.Should().BeOfType<BadRequestObjectResult>().Which.Value.Should().BeOfType<string>()
                .Which.Should().Contain("Point 0").And.Contain("'Current'");
            (await CountRowsAsync(childTenantId, archiveRtId)).Should().Be(0);
        }
        finally
        {
            await DropChildIfExistingAsync(childTenantId);
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<InsertRawPointRestDto> Bind(string body) =>
        JsonSerializer.Deserialize<IReadOnlyList<InsertRawPointRestDto>>(body, MvcBodyOptions)
        ?? throw new InvalidOperationException("The body deserialized to null.");

    /// <summary>Posts one row of <paramref name="rtId" />; a non-null reason makes it a manual write.</summary>
    private async Task<StreamDataWriteResultRestDto> WriteAsync(StreamDataController controller, string tenantId,
        OctoObjectId archiveRtId, OctoObjectId rtId, int reading, double voltage, string? manualReason)
    {
        var manual = manualReason is null ? "" : $", \"origin\": \"manual\", \"reason\": \"{manualReason}\"";
        var points = Bind($$"""
            [{ "rtId": "{{rtId}}", "ckTypeId": "{{fixture.TestCkTypeId}}", "timestamp": "{{Timestamp:O}}",
               "attributes": { "MeterReading": {{reading}}, "Voltage": {{voltage.ToString(CultureInfo.InvariantCulture)}} } {{manual}} }]
            """);

        var result = await controller.Insert(tenantId, archiveRtId.ToString(), points);

        return result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<StreamDataWriteResultRestDto>().Subject;
    }

    /// <summary>Enables stream data on the child, imports the test CK model and activates a raw archive.</summary>
    private async Task<OctoObjectId> PrepareRawArchiveAsync(string childTenantId)
    {
        var child = await GetChildAsync(childTenantId);
        await child.EnableStreamDataAsync();
        var import = new OperationResult();
        await child.ImportCkModelAsync(new CkModelId("AssetRepositoryIntegrationTest"), import);
        import.HasErrors.Should().BeFalse(string.Join(", ", import.Messages.Select(m => m.MessageText)));

        var archive = new RtRawArchive
        {
            RtWellKnownName = "RestInsertRawArchive",
            TargetCkTypeId = fixture.TestCkTypeId,
            Status = RtCkArchiveStatusEnum.Created,
            Columns = new AttributeRecordValueList<RtCkArchiveColumnRecord>
            {
                new() { Path = "MeterReading", Indexed = true, Required = false },
                new() { Path = "Voltage", Indexed = true, Required = false }
            }
        };
        var repository = child.GetTenantRepository();
        using (var session = await repository.GetSessionAsync())
        {
            session.StartTransaction();
            await repository.InsertOneRtEntityAsync(session, archive);
            await session.CommitTransactionAsync();
        }

        var lifecycle = child.GetArchiveLifecycleService()
            ?? throw new InvalidOperationException("ArchiveLifecycleService not registered.");
        await lifecycle.ActivateAsync(archive.RtId);
        return archive.RtId;
    }

    private StreamDataController ControllerFor(params (string Type, string Value)[] claims)
    {
        var controller = new StreamDataController(
            A.Fake<ILogger<StreamDataController>>(), fixture.GetSystemContext(), A.Fake<IHostApplicationLifetime>());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "Bearer"))
            }
        };
        return controller;
    }

    private async Task<Dictionary<string, object?>> ReadRowAsync(string schema, OctoObjectId archiveRtId, OctoObjectId rtId)
    {
        await CountRowsAsync(schema, archiveRtId);   // refreshes the table
        var table = $"\"{schema}\".\"archive_{archiveRtId}\"";
        await using var connection = new NpgsqlConnection(fixture.CrateDbConnectionString);
        await connection.OpenAsync();
        await using var select = new NpgsqlCommand(
            $"SELECT \"meterreading\", \"voltage\", \"row_origin\", \"manual_written_by\", \"manual_reason\" FROM {table} WHERE \"rtid\" = @rtid",
            connection);
        select.Parameters.AddWithValue("rtid", rtId.ToString());
        await using var reader = await select.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue("the row must have been inserted");
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        }

        (await reader.ReadAsync()).Should().BeFalse("all writes address the same row");
        return row;
    }

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
