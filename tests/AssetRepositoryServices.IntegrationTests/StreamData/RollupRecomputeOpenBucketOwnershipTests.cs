using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.StreamData;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
/// AB#5189 item B — against a REAL CrateDB: a recompute must not claim a bucket the forward
/// aggregation is still refreshing, because the recompute's generation pointer outranks the forward
/// pass's generation-0 write on the read path. The forward pass keeps re-writing a bucket until
/// <c>bucketEnd &lt;= now - WatermarkLag</c> (AB#4306); the recompute stops at the start of the
/// bucket containing <c>now</c>, ignoring the lag. Every bucket in between belongs to both, and the
/// recompute's value wins permanently — so the late data the watermark lag exists to absorb never
/// reaches the series.
/// </summary>
[Collection(StreamDataCollection.Name)]
public class RollupRecomputeOpenBucketOwnershipTests(StreamDataFixture fixture, ITestOutputHelper output)
{
    private static readonly TimeSpan BucketSize = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan WatermarkLag = TimeSpan.FromMinutes(5);

    // The contested bucket, and a "now" two minutes into the following one: 11:00 > 11:02 - 5min,
    // so the forward pass still treats [10:45, 11:00) as its provisional open bucket.
    private static readonly DateTime ContestedBucketStart = new(2026, 1, 1, 10, 45, 0, DateTimeKind.Utc);
    private static readonly DateTime ContestedBucketEnd = new(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 1, 1, 11, 2, 0, DateTimeKind.Utc);
    private static readonly DateTime RecomputeFrom = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Recompute_LeavesTheLaggedOpenBucketToTheForwardPass_SoLateDataStillLands()
    {
        fixture.OutputHelper = output;

        var systemContext = fixture.GetSystemContext();
        var tenantContext = await systemContext.FindTenantContextAsync(systemContext.TenantId);

        var rollupLifecycle = tenantContext.GetRollupArchiveLifecycleService()
            ?? throw new InvalidOperationException("Rollup lifecycle service not available.");
        var archiveLifecycle = tenantContext.GetArchiveLifecycleService()
            ?? throw new InvalidOperationException("Archive lifecycle service not available.");
        var archiveStore = tenantContext.GetArchiveRuntimeStore();
        var rollupStore = tenantContext.GetRollupArchiveRuntimeStore()
            ?? throw new InvalidOperationException("Rollup runtime store not available.");
        var repo = tenantContext.GetStreamDataRepository()
            ?? throw new InvalidOperationException("StreamData repository not available.");
        var executor = (IArchiveRecomputeExecutor)repo;

        // ── An isolated source archive with one metering point in the contested bucket ──────────
        // Its own archive, not the shared fixture one: this test writes a second, LATE point into
        // that bucket, and doing so in the fixture's archive would move every other test's numbers.
        var builder = new MultiSourceArchiveBuilder(fixture);
        var sourceRtId = await builder.CreateRawArchiveAsync("Ab5189OpenBucketSource");
        var meteringPointRtId = OctoObjectId.GenerateNewId();
        await builder.InsertPointsAsync(
            sourceRtId, meteringPointRtId, [(ContestedBucketStart.AddMinutes(5), 100.0)]);

        // ── A rollup with a 5-minute watermark lag ───────────────────────────────────────────────
        var rollupRtId = await rollupLifecycle.CreateAsync(
            rtWellKnownName: $"Ab5189OpenBucketOwnershipRollup{Guid.NewGuid():N}",
            sources: [new RollupSourceReference(sourceRtId)],
            bucketSize: BucketSize,
            watermarkLag: WatermarkLag,
            aggregations: new[] { new CkRollupAggregationSpec("Voltage", CkRollupFunction.Sum, null) });
        await archiveLifecycle.ActivateAsync(rollupRtId);

        var sourceSnapshot = await archiveStore.GetAsync(sourceRtId)
            ?? throw new InvalidOperationException("Source archive snapshot missing.");
        var rollupSnapshot = await rollupStore.GetAsync(rollupRtId)
            ?? throw new InvalidOperationException("Rollup archive snapshot missing.");

        // ── 1. The forward pass writes the still-open bucket provisionally (generation 0) ────────
        await repo.AggregateBucketAsync(
            sourceSnapshot, rollupSnapshot, ContestedBucketStart, ContestedBucketEnd, CancellationToken.None);
        await fixture.RefreshArchiveTableAsync(rollupRtId.ToString());

        (await ReadVoltageSumAsync(repo, rollupRtId, rollupSnapshot, meteringPointRtId))
            .Should().Be(100.0, "the forward pass aggregated the single point in the open bucket");

        // ── 2. A recompute reaching up to "now" runs while that bucket is still open ─────────────
        var recompute = new RecomputeOrchestrator(
            systemContext.TenantId,
            archiveStore,
            rollupStore,
            new RollupDependencyGraph(rollupStore),
            tenantContext.GetArchiveRecomputeStateStore(),
            tenantContext.GetRecomputeJobStore(),
            executor,
            repo,
            new LoggingArchiveAuditTrail(NullLogger<LoggingArchiveAuditTrail>.Instance),
            NullLogger<RecomputeOrchestrator>.Instance,
            () => Now);

        var job = await recompute.RecomputeArchiveAsync(
            rollupRtId, RecomputeFrom, Now, null, RecomputeTrigger.Manual, CancellationToken.None);
        job.State.Should().Be(RecomputeJobState.Completed);
        await fixture.RefreshArchiveTableAsync(rollupRtId.ToString());
        await RefreshAsync($"\"{fixture.StreamDataTenantId}\".\"archive_{rollupRtId}__genmap\"");

        // ── 3. The late value the watermark lag exists to absorb arrives ────────────────────────
        await builder.InsertPointsAsync(
            sourceRtId, meteringPointRtId, [(ContestedBucketStart.AddMinutes(10), 50.0)]);

        // ── 4. The forward pass refreshes the still-open bucket again, now with both points ──────
        await repo.AggregateBucketAsync(
            sourceSnapshot, rollupSnapshot, ContestedBucketStart, ContestedBucketEnd, CancellationToken.None);
        await fixture.RefreshArchiveTableAsync(rollupRtId.ToString());

        // ── 5. The read path must show the complete bucket ───────────────────────────────────────
        (await ReadVoltageSumAsync(repo, rollupRtId, rollupSnapshot, meteringPointRtId))
            .Should().Be(150.0,
                "the forward pass owns the bucket until it is lag-closed; if the recompute claimed it, " +
                "its generation pointer masks the forward pass's later, complete value and the late " +
                "point is lost from the series");
    }

    private static async Task<double?> ReadVoltageSumAsync(
        IStreamDataRepository repo,
        OctoObjectId rollupRtId,
        RollupArchiveSnapshot rollupSnapshot,
        OctoObjectId meteringPointRtId)
    {
        var result = await repo.ExecuteQueryAsync(rollupRtId, StreamDataQueryOptions.Create()
            .WithCkTypeId(rollupSnapshot.TargetCkTypeId)
            .WithColumns(new List<string> { "voltage_sum" })
            .WithRtIds(new List<OctoObjectId> { meteringPointRtId })
            .WithTimeRange(ContestedBucketStart, ContestedBucketEnd)
            .WithPagination(0, 100));

        var row = result.Rows.SingleOrDefault();
        return row?.Values.TryGetValue("voltage_sum", out var v) == true
            ? Convert.ToDouble(v)
            : null;
    }

    private async Task RefreshAsync(string qualifiedTable)
    {
        await using var conn = new Npgsql.NpgsqlConnection(fixture.CrateDbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand($"REFRESH TABLE {qualifiedTable}", conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
