using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
/// AB#4772 — self-heal for the inherited mandatory <c>Archive.Columns</c> attribute on rollup
/// archives. Entities seeded via ImportRt can lack the attribute entirely (the API create path is
/// the only one that persists the derived aggregate columns), which breaks the non-null
/// <c>columns</c> GraphQL field for the whole archives list (AB#4771, prod-1/energyiq). These
/// tests pin <see cref="IRollupArchiveRuntimeStore.TryPersistDerivedColumnsAsync"/> against the
/// real MongoDB write path: the seeded shape heals, computed columns survive, and the call is
/// idempotent on healthy entities.
/// </summary>
[Collection(StreamDataCollection.Name)]
public class RollupColumnsSelfHealTests(StreamDataFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task TryPersistDerivedColumns_EntityWithoutColumnsAttribute_HealsToGeneratorOutput()
    {
        fixture.OutputHelper = output;

        var systemContext = fixture.GetSystemContext();
        var tenantContext = await systemContext.FindTenantContextAsync(systemContext.TenantId);
        var rollupStore = tenantContext.GetRollupArchiveRuntimeStore()
            ?? throw new InvalidOperationException("Rollup runtime store not available.");
        var rollupLifecycle = tenantContext.GetRollupArchiveLifecycleService()
            ?? throw new InvalidOperationException("Rollup lifecycle service not available.");
        var repo = tenantContext.GetTenantRepository();
        var session = await repo.GetSessionAsync();

        // The typed insert path correctly REJECTS a missing mandatory Columns attribute (verified
        // by this test suite's history) — only ImportRt bypasses that validation. So: create a
        // healthy rollup via the API path, then $unset the columns attribute with a raw driver
        // update to produce the exact AB#4771 seed-defect shape.
        var rollupRtId = await rollupLifecycle.CreateAsync(
            rtWellKnownName: "SelfHealSeededRollup",
            sourceArchiveRtId: fixture.ArchiveRtId,
            bucketSize: TimeSpan.FromMinutes(5),
            watermarkLag: TimeSpan.Zero,
            aggregations: new[]
            {
                new CkRollupAggregationSpec("Voltage", CkRollupFunction.Avg, null),
                new CkRollupAggregationSpec("Voltage", CkRollupFunction.Max, null),
            });
        await UnsetColumnsAttributeAsync(rollupRtId);

        var before = await rollupStore.GetAsync(rollupRtId);
        before.Should().NotBeNull();
        before!.HasPersistedColumns.Should().BeFalse("the entity carries no Columns attribute");

        var healed = await rollupStore.TryPersistDerivedColumnsAsync(rollupRtId);
        healed.Should().BeTrue("the missing aggregate columns must be persisted");

        var after = await rollupStore.GetAsync(rollupRtId);
        after!.HasPersistedColumns.Should().BeTrue();

        // The persisted columns must match the RollupColumnGenerator derivation:
        // AVG expands to {base}_sum + {base}_count, MAX stays single.
        var entity = (RtRollupArchive?)await repo.GetRtEntityByRtIdAsync<RtArchive>(session, rollupRtId);
        entity!.Columns.Should().NotBeNull();
        entity.Columns!.Select(c => c.GetAttributeStringValueOrDefault("Path")).Should().BeEquivalentTo(
            "voltage_avg_sum", "voltage_avg_count", "voltage_max");

        // Idempotency: a healthy entity is a no-op.
        (await rollupStore.TryPersistDerivedColumnsAsync(rollupRtId)).Should().BeFalse();
    }

    /// <summary>
    /// Reproduces the ImportRt seed defect via a raw driver <c>$unset</c> — the typed repository
    /// paths (correctly) refuse to write an entity without the mandatory Columns attribute.
    /// </summary>
    private async Task UnsetColumnsAttributeAsync(OctoObjectId rollupRtId)
    {
        var client = new MongoClient(fixture.GetConnectionString());
        var filter = new BsonDocument("_id", ObjectId.Parse(rollupRtId.ToString()));
        var unset = new BsonDocument("$unset", new BsonDocument("attributes.columns", 1));

        foreach (var dbName in await (await client.ListDatabaseNamesAsync()).ToListAsync())
        {
            var collection = client.GetDatabase(dbName)
                .GetCollection<BsonDocument>("RtEntity_SystemStreamDataArchive");
            var result = await collection.UpdateOneAsync(filter, unset);
            if (result.MatchedCount == 1)
            {
                return;
            }
        }

        throw new InvalidOperationException(
            $"Rollup entity {rollupRtId} not found in any RtEntity_SystemStreamDataArchive collection.");
    }

    [Fact]
    public async Task TryPersistDerivedColumns_ComputedOnlyColumns_HealsAndPreservesComputed()
    {
        fixture.OutputHelper = output;

        var systemContext = fixture.GetSystemContext();
        var tenantContext = await systemContext.FindTenantContextAsync(systemContext.TenantId);
        var rollupStore = tenantContext.GetRollupArchiveRuntimeStore()
            ?? throw new InvalidOperationException("Rollup runtime store not available.");
        var rollupLifecycle = tenantContext.GetRollupArchiveLifecycleService()
            ?? throw new InvalidOperationException("Rollup lifecycle service not available.");
        var repo = tenantContext.GetTenantRepository();
        var session = await repo.GetSessionAsync();

        // Properly created rollup (columns persisted by the API path)…
        var rollupRtId = await rollupLifecycle.CreateAsync(
            rtWellKnownName: "SelfHealComputedRollup",
            sourceArchiveRtId: fixture.ArchiveRtId,
            bucketSize: TimeSpan.FromMinutes(5),
            watermarkLag: TimeSpan.Zero,
            aggregations: new[] { new CkRollupAggregationSpec("Voltage", CkRollupFunction.Sum, null) });

        // …then strip it down to a computed-only Columns list (a computed column alone must not
        // count as "persisted" — it is a user addition on top of the aggregate columns).
        var entity = (RtRollupArchive?)await repo.GetRtEntityByRtIdAsync<RtArchive>(session, rollupRtId);
        var computedOnly = new AttributeRecordValueList<RtCkArchiveColumnRecord>();
        computedOnly.Add(new RtCkArchiveColumnRecord
        {
            Name = "double_voltage",
            Formula = "voltage_sum * 2",
            ResultType = RtCkComputedColumnResultTypeEnum.Double,
        });
        entity!.Columns = computedOnly;
        await repo.UpdateOneRtEntityByIdAsync<RtRollupArchive>(session, rollupRtId, entity);

        (await rollupStore.GetAsync(rollupRtId))!.HasPersistedColumns.Should().BeFalse(
            "computed-only columns are not the dehydrated aggregate cache");

        (await rollupStore.TryPersistDerivedColumnsAsync(rollupRtId)).Should().BeTrue();

        var healedEntity = (RtRollupArchive?)await repo.GetRtEntityByRtIdAsync<RtArchive>(session, rollupRtId);
        healedEntity!.Columns!.Select(c => c.GetAttributeStringValueOrDefault("Path"))
            .Should().Contain("voltage_sum");
        healedEntity.Columns!
            .Where(c => !string.IsNullOrWhiteSpace(c.GetAttributeStringValueOrDefault("Formula")))
            .Select(c => c.GetAttributeStringValueOrDefault("Name"))
            .Should().ContainSingle().Which.Should().Be("double_voltage");
    }
}
