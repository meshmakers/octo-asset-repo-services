using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
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

    // ── AB#5157: multi-source delete guard + per-source disable ───────────────────────────────

    [Fact]
    public async Task DeletingASourceIsRefused_ForTheLegacyScalar_TheNativeList_AndAnExpiredSpan()
    {
        // The guard is membership in the NORMALISED source list, so all three declaration shapes
        // protect their source equally: a rollup stored with the deprecated SourceArchiveRtId
        // scalar (the ImportRt / pre-1.8.0 shape), a rollup declaring the source natively, and a
        // source whose validity span ended long ago — its history is still in the rollup, so it is
        // just as much in use as the currently authoritative one.
        fixture.OutputHelper = output;
        var legacySource = await CreateRawArchiveAsync("GuardLegacySource");
        var pastSource = await CreateRawArchiveAsync("GuardPastSource");
        var currentSource = await CreateRawArchiveAsync("GuardCurrentSource");

        var legacyRollup = await CreateRollupAsync("GuardLegacyRollup", [new RollupSourceReference(legacySource)]);
        await SeedLegacyScalarSourceAsync(legacyRollup, legacySource);
        var multiRollup = await CreateRollupAsync("GuardMultiRollup",
        [
            new RollupSourceReference(pastSource, ValidTo: Cutover),
            new RollupSourceReference(currentSource, ValidFrom: Cutover),
        ]);

        (await LoadRollupAsync(legacyRollup)).SingleUnboundedSourceRtId.Should().Be(legacySource,
            "the deprecated scalar normalises into one unbounded source on read");

        foreach (var source in new[] { legacySource, pastSource, currentSource })
        {
            var refusal = await Assert.ThrowsAsync<RollupSourceInUseException>(() => DeleteArchiveAsync(source));
            refusal.DependentRollupCount.Should().Be(1);
            refusal.Message.Should().Contain($"Source archive '{source}' has 1 active rollup(s) attached")
                .And.Contain("Delete or freeze them first.");
            (await ArchiveExistsAsync(source)).Should().BeTrue("a refused delete leaves the archive alone");
        }

        await DeleteArchiveAsync(legacyRollup);
        await DeleteArchiveAsync(multiRollup);

        foreach (var source in new[] { legacySource, pastSource, currentSource })
        {
            await DeleteArchiveAsync(source);
            (await ArchiveExistsAsync(source)).Should().BeFalse("the last referencing rollup is gone");
        }
    }

    [Fact]
    public async Task DisablingOneSourceOfAnActivatedRollup_StallsTheTickAtItsFirstBucket_AndResumesOnEnable()
    {
        // Disabling a source of an ALREADY activated rollup stays allowed (only activation is gated)
        // — but the orchestrator must not skip past the buckets that source owes: it stops with the
        // watermark unchanged so the data is still picked up once the source comes back.
        fixture.OutputHelper = output;
        var hourNow = AlignDownToHour(DateTime.UtcNow);
        var start = hourNow.AddHours(-3);
        var cutover = hourNow.AddHours(-1);

        var legacy = await CreateRawArchiveAsync("StallLegacy");
        var native = await CreateRawArchiveAsync("StallNative");
        await InsertHourlyPointsAsync(legacy, start.AddMinutes(30), cutover.AddMinutes(-30));
        await InsertHourlyPointsAsync(native, cutover.AddMinutes(30), cutover.AddMinutes(30));

        var rollupRtId = await CreateRollupAsync("StallRollup",
        [
            new RollupSourceReference(legacy, ValidTo: cutover),
            new RollupSourceReference(native, ValidFrom: cutover),
        ]);
        var lifecycle = await ArchiveLifecycleAsync();
        await lifecycle.ActivateAsync(rollupRtId);

        await lifecycle.DisableAsync(native);
        await RewindAsync(rollupRtId, start);

        var stalled = await TickAsync(rollupRtId);

        stalled.Should().Be(2, "the two buckets the still-activated legacy source covers are committed");
        (await LoadRollupAsync(rollupRtId)).LastAggregatedBucketEnd.Should().Be(cutover,
            "the tick stops at the disabled source's first bucket with the watermark unchanged");

        await lifecycle.EnableAsync(native);
        var resumed = await TickAsync(rollupRtId);

        resumed.Should().BeGreaterThanOrEqualTo(1, "the stalled bucket is picked up once the source is back");
        (await LoadRollupAsync(rollupRtId)).LastAggregatedBucketEnd
            .Should().NotBeNull().And.Subject.As<DateTime>()
            .Should().BeOnOrAfter(hourNow, "the previously refused bucket is now committed");
    }

    // ── AB#5157 helpers (system tenant) ───────────────────────────────────────────────────────

    /// <summary>Bucket-aligned cutover far in the past, used by the delete-guard spans.</summary>
    private static readonly DateTime Cutover = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly TimeSpan OneHour = TimeSpan.FromHours(1);

    private static DateTime AlignDownToHour(DateTime value) =>
        new(value.Year, value.Month, value.Day, value.Hour, 0, 0, DateTimeKind.Utc);

    private async Task<ITenantContext> TenantAsync()
    {
        var systemContext = fixture.GetSystemContext();
        return await systemContext.FindTenantContextAsync(systemContext.TenantId);
    }

    private async Task<IArchiveLifecycleService> ArchiveLifecycleAsync() =>
        (await TenantAsync()).GetArchiveLifecycleService()
        ?? throw new InvalidOperationException("ArchiveLifecycleService not registered.");

    private async Task<OctoObjectId> CreateRawArchiveAsync(string namePrefix)
    {
        var archive = new RtRawArchive
        {
            RtWellKnownName = $"{namePrefix}{Guid.NewGuid():N}",
            TargetCkTypeId = fixture.TestCkTypeId,
            Status = RtCkArchiveStatusEnum.Created,
            Columns = new AttributeRecordValueList<RtCkArchiveColumnRecord>
            {
                new() { Path = "Voltage", Indexed = true, Required = false },
            },
        };

        var repository = fixture.GetSystemContext().GetSystemTenantRepository();
        using (var session = await repository.GetSessionAsync())
        {
            session.StartTransaction();
            await repository.InsertOneRtEntityAsync(session, archive);
            await session.CommitTransactionAsync();
        }

        await (await ArchiveLifecycleAsync()).ActivateAsync(archive.RtId);
        return archive.RtId;
    }

    private async Task<OctoObjectId> CreateRollupAsync(
        string namePrefix, IReadOnlyList<RollupSourceReference> sources)
    {
        var tenantContext = await TenantAsync();
        return await tenantContext.GetRollupArchiveLifecycleService()!.CreateAsync(
            $"{namePrefix}{Guid.NewGuid():N}",
            sources,
            OneHour,
            TimeSpan.Zero,
            [new CkRollupAggregationSpec("Voltage", CkRollupFunction.Sum, null)]);
    }

    /// <summary>
    /// Rewrites the rollup entity into the pre-1.8.0 storage shape — the deprecated
    /// <c>SourceArchiveRtId</c> scalar with an empty <c>Sources</c> list — exactly as an
    /// <c>ImportRt</c> seed of a legacy dump leaves it.
    /// </summary>
    private async Task SeedLegacyScalarSourceAsync(OctoObjectId rollupRtId, OctoObjectId sourceArchiveRtId)
    {
        var repository = fixture.GetSystemContext().GetSystemTenantRepository();
        using var session = await repository.GetSessionAsync();
        var entity = await repository.GetRtEntityByRtIdAsync<RtRollupArchive>(session, rollupRtId);
        entity.Should().NotBeNull();
        entity!.SourceArchiveRtId = sourceArchiveRtId.ToString();
        entity.Sources = new AttributeRecordValueList<RtCkRollupSourceReferenceRecord>();
        await repository.UpdateOneRtEntityByIdAsync(session, rollupRtId, entity);
    }

    private async Task InsertHourlyPointsAsync(OctoObjectId archiveRtId, DateTime from, DateTime to)
    {
        var tenantContext = await TenantAsync();
        var repo = tenantContext.GetStreamDataRepository()!;
        var ckTypeId = new RtCkId<CkTypeId>(fixture.TestCkTypeId);
        var seriesRtId = OctoObjectId.GenerateNewId();

        var points = new List<StreamDataPoint>();
        var voltage = 230.0;
        for (var timestamp = from; timestamp <= to; timestamp = timestamp.Add(OneHour))
        {
            points.Add(new StreamDataPoint
            {
                RtId = seriesRtId,
                CkTypeId = ckTypeId,
                Timestamp = timestamp,
                RtWellKnownName = "StallSeries",
                Attributes = new Dictionary<string, object?> { ["voltage"] = voltage },
            });
            voltage += 1.0;
        }

        await repo.InsertAsync(archiveRtId, points);
        await fixture.RefreshArchiveTableAsync(archiveRtId.ToString());
    }

    private async Task<int> TickAsync(OctoObjectId rollupRtId) =>
        await (await TenantAsync()).GetRollupOrchestrator()!
            .ProcessRollupAsync(rollupRtId, TestContext.Current.CancellationToken);

    private async Task RewindAsync(OctoObjectId rollupRtId, DateTime toBucketEnd) =>
        await (await TenantAsync()).GetRollupOrchestrator()!.RewindWatermarkAsync(rollupRtId, toBucketEnd);

    private async Task<RollupArchiveSnapshot> LoadRollupAsync(OctoObjectId rollupRtId)
    {
        var snapshot = await (await TenantAsync()).GetRollupArchiveRuntimeStore()!.GetAsync(rollupRtId);
        snapshot.Should().NotBeNull();
        return snapshot!;
    }

    private async Task DeleteArchiveAsync(OctoObjectId archiveRtId) =>
        await (await ArchiveLifecycleAsync()).DeleteAsync(archiveRtId);

    private async Task<bool> ArchiveExistsAsync(OctoObjectId archiveRtId) =>
        await (await TenantAsync()).GetArchiveRuntimeStore().GetAsync(archiveRtId) is not null;

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
