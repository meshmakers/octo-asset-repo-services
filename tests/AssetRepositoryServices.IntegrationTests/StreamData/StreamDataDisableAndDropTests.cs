using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Npgsql;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
/// AB#4255 against a real CrateDB: the tenant-level disable is refused while an archive is still
/// Activated (the fixture's system tenant owns one), and dropping a tenant drops its CrateDB
/// namespace - proven on a temporary child tenant whose activated archive table disappears with it.
/// </summary>
[Collection("Sequential")]
public class StreamDataDisableAndDropTests(StreamDataFixture fixture, ITestOutputHelper output)
    : IClassFixture<StreamDataFixture>
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
    public async Task DropChildTenant_DropsItsCrateDbNamespace()
    {
        fixture.OutputHelper = output;
        const string childTenantId = "streamdropchild";
        var systemContext = fixture.GetSystemContext();

        using (var session = await systemContext.GetAdminSessionAsync())
        {
            session.StartTransaction();
            await systemContext.CreateChildTenantAsync(session, childTenantId, childTenantId);
            await session.CommitTransactionAsync();
        }

        try
        {
            ITenantContext child;
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                child = await systemContext.GetChildTenantContextAsync(session, childTenantId);
                await session.CommitTransactionAsync();
            }

            await child.EnableStreamDataAsync();
            var import = new OperationResult();
            await child.ImportCkModelAsync(new CkModelId("AssetRepositoryIntegrationTest"), import);
            import.HasErrors.Should().BeFalse(string.Join(", ", import.Messages.Select(m => m.MessageText)));

            var archive = new RtRawArchive
            {
                RtWellKnownName = "DropProbeArchive",
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
            (await CountTablesAsync(childTenantId)).Should().Be(1, "activation provisions the archive table");

            // Nothing live any more -> the tenant-level disable succeeds, the table is still there.
            await lifecycle.DisableAsync(archive.RtId);
            await child.DisableStreamDataAsync();
            (await CountTablesAsync(childTenantId)).Should().Be(1, "a disabled archive keeps its table");

            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.DropChildTenantAsync(session, childTenantId);
                await session.CommitTransactionAsync();
            }

            (await CountTablesAsync(childTenantId)).Should().Be(0, "the tenant drop drops the CrateDB namespace");
        }
        finally
        {
            using var session = await systemContext.GetAdminSessionAsync();
            session.StartTransaction();
            if (await systemContext.IsChildTenantExistingAsync(session, childTenantId))
            {
                await systemContext.DropChildTenantAsync(session, childTenantId);
            }

            await session.CommitTransactionAsync();
        }
    }

    private async Task<long> CountTablesAsync(string schema)
    {
        await using var connection = new NpgsqlConnection(fixture.CrateDbConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = @schema", connection);
        command.Parameters.AddWithValue("schema", schema);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }
}
