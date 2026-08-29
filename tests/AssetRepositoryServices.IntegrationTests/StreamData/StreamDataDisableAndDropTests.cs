using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Npgsql;
using Xunit;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
/// AB#4255 against a real CrateDB: the tenant-level disable is refused while an archive is still
/// Activated (the fixture's system tenant owns one); dropping a tenant for good drops the CrateDB
/// tables of exactly its own archives - proven on temporary child tenants: the table disappears with
/// the tenant, a database swap keeps it, and two tenants that share a CrateDB schema (ids differing
/// only in <c>-</c>/<c>_</c>) do not take each other's tables along.
/// </summary>
[Collection(StreamDataLifecycleCollection.Name)]
public class StreamDataDisableAndDropTests(StreamDataFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task DisableStreamData_IsRefused_WhileTheFixtureArchiveIsActivated()
    {
        fixture.OutputHelper = output;
        var systemContext = fixture.GetSystemContext();
        var tenantContext = await systemContext.FindTenantContextAsync(systemContext.TenantId);

        var refusal = await Assert.ThrowsAsync<StreamDataDisableBlockedException>(
            () => tenantContext.DisableStreamDataAsync());

        refusal.Message.Should().Contain("RawArchive 'MeteringPointArchive' (Activated)")
            .And.Contain("TimeRangeArchive 'WindowedMeteringPointArchive' (Activated)");
        (await tenantContext.IsStreamDataEnabledAsync()).Should().BeTrue("a refused disable leaves the flag alone");
    }

    [Fact]
    public async Task DropChildTenant_ForGood_DropsItsArchiveTables()
    {
        fixture.OutputHelper = output;
        const string childTenantId = "streamdropchild";
        await CreateChildAsync(childTenantId);

        try
        {
            var child = await GetChildAsync(childTenantId);
            var archive = await ActivateProbeArchiveAsync(child, "DropProbeArchive");
            (await ListTablesAsync(childTenantId)).Should().ContainSingle("activation provisions the archive table");

            // Nothing live any more -> the tenant-level disable succeeds, the table is still there.
            var lifecycle = child.GetArchiveLifecycleService()
                ?? throw new InvalidOperationException("ArchiveLifecycleService not registered.");
            await lifecycle.DisableAsync(archive);
            await child.DisableStreamDataAsync();
            (await ListTablesAsync(childTenantId)).Should().ContainSingle("a disabled archive keeps its table");

            await DropChildAsync(childTenantId, dropStreamData: true);

            (await ListTablesAsync(childTenantId)).Should().BeEmpty("dropping the tenant for good drops its tables");
        }
        finally
        {
            await DropChildIfExistingAsync(childTenantId);
        }
    }

    [Fact]
    public async Task DropChildTenant_ForADatabaseSwap_KeepsItsArchiveTables()
    {
        // The restore-over-existing-tenant contract: RestoreTenantAsync drops the database with the
        // default (dropStreamData: false) and restores it; the same archives exist afterwards and must
        // find their tables again - a Mongo-only restore must not lose the stream data.
        fixture.OutputHelper = output;
        const string childTenantId = "streamdropswap";
        await CreateChildAsync(childTenantId);
        var expectedTable = string.Empty;

        try
        {
            var child = await GetChildAsync(childTenantId);
            var archive = await ActivateProbeArchiveAsync(child, "SwapProbeArchive");
            expectedTable = $"archive_{archive}";

            await DropChildAsync(childTenantId, dropStreamData: false);

            (await ListTablesAsync(childTenantId)).Should().Equal(expectedTable);
        }
        finally
        {
            await DropChildIfExistingAsync(childTenantId);
            await DropTableAsync(childTenantId, expectedTable);
        }
    }

    [Fact]
    public async Task DropChildTenant_LeavesTheTablesOfATenantSharingTheSchemaAlone()
    {
        // TenantSchema.SchemaName strips '-' and '_': both tenants live in the CrateDB schema "dropcoll".
        // A schema-wide drop would have taken the neighbour's data with it (the reviewer's finding 3).
        fixture.OutputHelper = output;
        const string schema = "dropcoll";
        const string deleted = "drop-coll";
        const string neighbour = "drop_coll";
        await CreateChildAsync(deleted, "dropcolldeleted");
        await CreateChildAsync(neighbour, "dropcollneighbour");

        try
        {
            var deletedArchive = await ActivateProbeArchiveAsync(await GetChildAsync(deleted), "DeletedArchive");
            var neighbourArchive = await ActivateProbeArchiveAsync(await GetChildAsync(neighbour), "NeighbourArchive");
            (await ListTablesAsync(schema)).Should().BeEquivalentTo($"archive_{deletedArchive}", $"archive_{neighbourArchive}");

            await DropChildAsync(deleted, dropStreamData: true);

            (await ListTablesAsync(schema)).Should().Equal($"archive_{neighbourArchive}");
        }
        finally
        {
            await DropChildIfExistingAsync(deleted);
            await DropChildIfExistingAsync(neighbour);
        }
    }

    /// <summary>Enables stream data on the child, imports the test model and activates a raw archive.</summary>
    private async Task<OctoObjectId> ActivateProbeArchiveAsync(ITenantContext child, string wellKnownName)
    {
        await child.EnableStreamDataAsync();
        var import = new OperationResult();
        await child.ImportCkModelAsync(new CkModelId("AssetRepositoryIntegrationTest"), import);
        import.HasErrors.Should().BeFalse(string.Join(", ", import.Messages.Select(m => m.MessageText)));

        var archive = new RtRawArchive
        {
            RtWellKnownName = wellKnownName,
            TargetCkTypeId = fixture.TestCkTypeId,
            Status = RtCkArchiveStatusEnum.Created,
            Columns = new AttributeRecordValueList<RtCkArchiveColumnRecord>
            {
                new() { Path = "Voltage", Indexed = true, Required = false },
            },
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

    private async Task CreateChildAsync(string tenantId, string? databaseName = null)
    {
        var systemContext = fixture.GetSystemContext();
        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();
        await systemContext.CreateChildTenantAsync(session, databaseName ?? tenantId, tenantId);
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

    private async Task DropChildAsync(string tenantId, bool dropStreamData)
    {
        var systemContext = fixture.GetSystemContext();
        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();
        await systemContext.DropChildTenantAsync(session, tenantId, dropStreamData);
        await session.CommitTransactionAsync();
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

    private async Task DropTableAsync(string schema, string table)
    {
        if (string.IsNullOrEmpty(table))
        {
            return;
        }

        await using var connection = new NpgsqlConnection(fixture.CrateDbConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP TABLE IF EXISTS \"{schema}\".\"{table}\"", connection);
        await command.ExecuteNonQueryAsync();
    }
}
