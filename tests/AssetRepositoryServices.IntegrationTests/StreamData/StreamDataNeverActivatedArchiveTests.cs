using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Npgsql;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
/// AB#5141 against a real CrateDB: an archive that was never activated has no table, whatever its
/// status says. Blueprints seed archives with <c>Archive.Status = 2</c> (Disabled) straight into the
/// runtime data (EnergyCommunity.Base: nine archives, the three legacy ones stay that way on
/// greenfield tenants), and the tenant backup / archive export must treat such an archive as
/// "no data" instead of failing with <c>RelationUnknown</c> (42P01). Pins the storage-layer contract
/// the bot-services jobs build on: <see cref="IStreamDataRepository.ExportRowsAsync"/> yields no
/// rows, <see cref="IStreamDataRepository.GetArchiveStatsAsync"/> reports the table as missing, and
/// once the archive has been activated (even if disabled again right away) both report the table,
/// so an empty table and a missing table stay distinguishable in a backup manifest.
/// </summary>
[Collection(StreamDataLifecycleCollection.Name)]
public class StreamDataNeverActivatedArchiveTests(StreamDataFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task SeededDisabledRawArchive_ExportsNoRows_AndReportsNoTable_UntilActivated()
    {
        fixture.OutputHelper = output;
        const string childTenantId = "ab5141raw";
        await CreateChildAsync(childTenantId);

        try
        {
            var child = await PrepareChildAsync(childTenantId);
            var archiveRtId = await SeedDisabledRawArchiveAsync(child, "SeededRawArchive");
            var repository = child.GetStreamDataRepository()
                ?? throw new InvalidOperationException("StreamDataRepository not available.");

            // Never activated: no table, whatever the status says.
            (await ListTablesAsync(childTenantId)).Should().BeEmpty("the archive was seeded Disabled, never activated");
            var stats = await repository.GetArchiveStatsAsync([archiveRtId], TestContext.Current.CancellationToken);
            stats[archiveRtId].TableExists.Should().BeFalse("the storage stats are what the tenant backup decides by");

            var rows = await ExportAllAsync(repository, archiveRtId, window: null);
            rows.Should().BeEmpty("an archive without a backing table yields no rows instead of failing with 42P01");

            // Activate once (provisions the table) and disable again: the seeded status, but WITH a table.
            var lifecycle = child.GetArchiveLifecycleService()
                ?? throw new InvalidOperationException("ArchiveLifecycleService not registered.");
            await lifecycle.ActivateAsync(archiveRtId);
            await lifecycle.DisableAsync(archiveRtId);

            (await ListTablesAsync(childTenantId)).Should().Equal($"archive_{archiveRtId}");
            stats = await repository.GetArchiveStatsAsync([archiveRtId], TestContext.Current.CancellationToken);
            stats[archiveRtId].TableExists.Should().BeTrue("activation provisioned the table");

            rows = await ExportAllAsync(repository, archiveRtId, window: null);
            rows.Should().BeEmpty("the table exists but holds no rows — same result, different manifest entry");

            // Second flavour: the tenant schema now exists (CrateDB creates it with the first table), but
            // this archive's table does not — CrateDB answers RelationUnknown (42P01) here instead of the
            // SchemaUnknown (XX000) of the schema-less tenant above. The staging-1 report was this one.
            var secondRtId = await SeedDisabledRawArchiveAsync(child, "SeededRawArchiveTwo");
            stats = await repository.GetArchiveStatsAsync([secondRtId], TestContext.Current.CancellationToken);
            stats[secondRtId].TableExists.Should().BeFalse();
            (await ExportAllAsync(repository, secondRtId, window: null)).Should().BeEmpty();
            (await ListTablesAsync(childTenantId)).Should().Equal(new[] { $"archive_{archiveRtId}" },
                "exporting must not provision anything");
        }
        finally
        {
            await DropChildIfExistingAsync(childTenantId);
        }
    }

    [Fact]
    public async Task SeededDisabledTimeRangeArchive_ExportsNoRows_WithAndWithoutWindow()
    {
        // The EnergyCommunity legacy archives (a11/a12/a13) are TimeRangeArchives seeded Disabled; the
        // windowed export path picks window_start as its time axis — irrelevant when there is no table.
        fixture.OutputHelper = output;
        const string childTenantId = "ab5141window";
        await CreateChildAsync(childTenantId);

        try
        {
            var child = await PrepareChildAsync(childTenantId);
            var archiveRtId = await SeedDisabledTimeRangeArchiveAsync(child, "SeededLegacyDailyArchive");
            var repository = child.GetStreamDataRepository()
                ?? throw new InvalidOperationException("StreamDataRepository not available.");

            (await repository.GetArchiveStatsAsync([archiveRtId], TestContext.Current.CancellationToken))[archiveRtId]
                .TableExists.Should().BeFalse();

            (await ExportAllAsync(repository, archiveRtId, window: null)).Should().BeEmpty();

            var window = new TimeWindow(
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
            (await ExportAllAsync(repository, archiveRtId, window)).Should().BeEmpty();
        }
        finally
        {
            await DropChildIfExistingAsync(childTenantId);
        }
    }

    [Fact]
    public async Task ImportRows_IntoASeededDisabledArchive_FailsWithRelationUnknown()
    {
        // Documents why ImportArchiveDataJob has to probe the table before importing: the storage layer
        // itself surfaces CrateDB's RelationUnknown / SchemaUnknown deep in the insert path. Disabled
        // alone (the §7.1 import precondition) does not imply a provisioned table.
        fixture.OutputHelper = output;
        const string childTenantId = "ab5141import";
        await CreateChildAsync(childTenantId);

        try
        {
            var child = await PrepareChildAsync(childTenantId);
            var archiveRtId = await SeedDisabledRawArchiveAsync(child, "SeededImportTarget");
            var repository = child.GetStreamDataRepository()
                ?? throw new InvalidOperationException("StreamDataRepository not available.");

            var row = new Dictionary<string, object?>
            {
                ["rtid"] = OctoObjectId.GenerateNewId().ToString(),
                ["cktypeid"] = fixture.TestCkTypeId,
                ["timestamp"] = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ["voltage"] = 230.0,
            };

            var failure = await Record.ExceptionAsync(() =>
                repository.ImportRowsAsync(archiveRtId, OneRow(row), ArchiveImportMode.InsertOnly,
                    TestContext.Current.CancellationToken));

            failure.Should().NotBeNull("there is no table to insert into");
            IsRelationUnknown(failure!).Should().BeTrue($"expected CrateDB's RelationUnknown, got: {failure}");
        }
        finally
        {
            await DropChildIfExistingAsync(childTenantId);
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Enables stream data on the child and imports the test CK model — no archive yet.</summary>
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
    /// Mirrors a blueprint seed: the archive entity is written with <c>Status = Disabled</c> directly,
    /// the lifecycle service never runs, so no CrateDB table is provisioned.
    /// </summary>
    private async Task<OctoObjectId> SeedDisabledRawArchiveAsync(ITenantContext child, string wellKnownName)
    {
        var archive = new RtRawArchive
        {
            RtWellKnownName = wellKnownName,
            TargetCkTypeId = fixture.TestCkTypeId,
            Status = RtCkArchiveStatusEnum.Disabled,
            Columns = new AttributeRecordValueList<RtCkArchiveColumnRecord>
            {
                new() { Path = "Voltage", Indexed = true, Required = false },
            },
        };
        await InsertAsync(child, archive);
        return archive.RtId;
    }

    private async Task<OctoObjectId> SeedDisabledTimeRangeArchiveAsync(ITenantContext child, string wellKnownName)
    {
        var archive = new RtTimeRangeArchive
        {
            RtWellKnownName = wellKnownName,
            TargetCkTypeId = fixture.TestCkTypeId,
            Status = RtCkArchiveStatusEnum.Disabled,
            Columns = new AttributeRecordValueList<RtCkArchiveColumnRecord>
            {
                new() { Path = "Voltage", Indexed = true, Required = false },
            },
            Period = TimeSpan.FromDays(1),
        };
        await InsertAsync(child, archive);
        return archive.RtId;
    }

    private static async Task InsertAsync(ITenantContext child, RtEntity archive)
    {
        var repository = child.GetTenantRepository();
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        await repository.InsertOneRtEntityAsync(session, archive);
        await session.CommitTransactionAsync();
    }

    private static async Task<List<IReadOnlyDictionary<string, object?>>> ExportAllAsync(
        IStreamDataRepository repository, OctoObjectId archiveRtId, TimeWindow? window)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        await foreach (var row in repository.ExportRowsAsync(archiveRtId, window, TestContext.Current.CancellationToken))
        {
            rows.Add(row);
        }

        return rows;
    }

    private static async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> OneRow(
        IReadOnlyDictionary<string, object?> row)
    {
        await Task.CompletedTask;
        yield return row;
    }

    /// <summary>
    /// CrateDB's "nothing to insert into": <c>RelationUnknown</c> (42P01) when the tenant schema exists
    /// but the table does not, <c>SchemaUnknown</c> (XX000, "Schema 'x' unknown") when no table of the
    /// tenant was ever provisioned, so the schema itself is missing.
    /// </summary>
    private static bool IsRelationUnknown(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: "42P01" })
            {
                return true;
            }

            var message = current.Message;
            if (message.Contains("RelationUnknown", StringComparison.OrdinalIgnoreCase)
                || (message.Contains("unknown", StringComparison.OrdinalIgnoreCase)
                    && (message.Contains("Relation", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("Schema", StringComparison.OrdinalIgnoreCase))))
            {
                return true;
            }
        }

        return false;
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

    private async Task<List<string>> ListTablesAsync(string schema)
    {
        await using var connection = new NpgsqlConnection(fixture.CrateDbConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = @schema ORDER BY table_name",
            connection);
        command.Parameters.AddWithValue("schema", schema);
        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }
}
