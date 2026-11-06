using System.Diagnostics;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Npgsql;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
/// AB#8002 against real MongoDB and CrateDB through the tenant context the service uses: adding a computed column queues
/// its backfill (column Backfilling, RecomputeJob Pending in the tenant's job store) and returns; the drain ends the column
/// Active and the job Completed; the rollup active-job lookup never returns the backfill job; a retry of an Active column
/// is refused.
/// </summary>
[Collection(StreamDataMutatingCollection.Name)]
public class ComputedColumnBackfillJobTests(StreamDataFixture fixture, ITestOutputHelper output)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EnqueuedAdd_ReturnsBackfilling_AndTheDrainCompletesTheJob()
    {
        fixture.OutputHelper = output;
        const string columnName = "queuedVoltage";
        var tenant = await TenantAsync();
        var lifecycle = tenant.GetArchiveLifecycleService()!;
        var store = tenant.GetArchiveRuntimeStore();
        var jobs = tenant.GetRecomputeJobStore();

        var job = await lifecycle.EnqueueAddComputedColumnAsync(
            fixture.ArchiveRtId, columnName, "Voltage * 4", FormulaResultType.Double, indexed: false, Ct);

        try
        {
            job.Should().NotBeNull();
            job!.ComputedColumnName.Should().Be(columnName);
            (await ColumnAsync(store, columnName)).ComputedState.Should().BeOneOf(
                [ComputedColumnState.Backfilling, ComputedColumnState.Active],
                "the call only queues; the background service may already have picked it up");
            (await jobs.GetActiveForArchiveAsync(fixture.ArchiveRtId))?.IsComputedColumnBackfill.Should().NotBe(true,
                "the rollup orchestrator must never see a backfill job as the archive's active recompute");

            await lifecycle.RunPendingComputedColumnBackfillsAsync(Ct);
            await WaitUntilAsync(async () => (await ColumnAsync(store, columnName)).ComputedState == ComputedColumnState.Active);

            var done = await jobs.GetAsync(job.RtId);
            done!.State.Should().Be(RecomputeJobState.Completed);
            done.RowsProcessed.Should().BeGreaterThan(0, "the fixture seeded rows into the archive");
            (await jobs.GetForArchiveAsync(fixture.ArchiveRtId, 50)).Should()
                .Contain(j => j.RtId == job.RtId && j.ComputedColumnName == columnName,
                    "the backfill job is listed by the recompute-jobs query");

            await FluentActions.Invoking(() => lifecycle.RetryComputedColumnBackfillAsync(fixture.ArchiveRtId, columnName, Ct))
                .Should().ThrowAsync<ComputedColumnInvalidException>("an Active column has nothing to retry");
        }
        finally
        {
            await lifecycle.RemoveComputedColumnAsync(fixture.ArchiveRtId, columnName);
        }
    }

    /// <summary>
    /// AB#8331 end to end against real CrateDB: cells of an active computed column that an aborted evaluation left NULL
    /// (simulated with an UPDATE) are re-evaluated by a repair job; the column stays Active, other cells are untouched.
    /// </summary>
    [Fact]
    public async Task Repair_RewritesTheNullCellsOfAnActiveColumn_AndKeepsItActive()
    {
        fixture.OutputHelper = output;
        const string columnName = "repairVoltage";
        var tenant = await TenantAsync();
        var lifecycle = tenant.GetArchiveLifecycleService()!;
        var store = tenant.GetArchiveRuntimeStore();
        var jobs = tenant.GetRecomputeJobStore();

        await lifecycle.EnqueueAddComputedColumnAsync(
            fixture.ArchiveRtId, columnName, "Voltage * 2", FormulaResultType.Double, indexed: false, Ct);
        try
        {
            await lifecycle.RunPendingComputedColumnBackfillsAsync(Ct);
            await WaitUntilAsync(async () => (await ColumnAsync(store, columnName)).ComputedState == ComputedColumnState.Active);
            var physical = columnName.ToLowerInvariant();
            var table = $"\"{fixture.StreamDataTenantId}\".\"archive_{fixture.ArchiveRtIdString}\"";

            // What the OOM left on prod-1: NULL cells although the operand is set.
            var nulled = await ExecuteAsync($"UPDATE {table} SET \"{physical}\" = NULL WHERE \"voltage\" < 225");
            nulled.Should().BeGreaterThan(0);
            await fixture.RefreshArchiveTableAsync(fixture.ArchiveRtIdString);

            var job = await lifecycle.RepairComputedColumnAsync(fixture.ArchiveRtId, columnName, cancellationToken: Ct);
            job.Should().NotBeNull();
            job!.IsComputedColumnRepair.Should().BeTrue();
            await lifecycle.RunPendingComputedColumnBackfillsAsync(Ct);
            await WaitUntilAsync(async () => (await jobs.GetAsync(job.RtId))!.State == RecomputeJobState.Completed);

            await fixture.RefreshArchiveTableAsync(fixture.ArchiveRtIdString);
            (await ScalarAsync($"SELECT COUNT(*) FROM {table} WHERE \"{physical}\" IS NULL AND \"voltage\" IS NOT NULL"))
                .Should().Be(0, "every NULL cell with a set operand was re-evaluated");
            (await ScalarAsync($"SELECT COUNT(*) FROM {table} WHERE \"{physical}\" <> \"voltage\" * 2"))
                .Should().Be(0, "the repaired cells hold the formula's value");
            (await jobs.GetAsync(job.RtId))!.RowsProcessed.Should().Be((int)nulled, "only the NULL cells were re-evaluated");
            (await ColumnAsync(store, columnName)).ComputedState.Should().Be(ComputedColumnState.Active);
        }
        finally
        {
            await lifecycle.RemoveComputedColumnAsync(fixture.ArchiveRtId, columnName);
        }
    }

    private async Task<long> ExecuteAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(fixture.CrateDbConnectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteNonQueryAsync(Ct);
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(fixture.CrateDbConnectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(Ct));
    }

    private async Task<ITenantContext> TenantAsync()
    {
        var systemContext = fixture.GetSystemContext();
        return await systemContext.FindTenantContextAsync(systemContext.TenantId);
    }

    private async Task<CkArchiveColumnSpec> ColumnAsync(IArchiveRuntimeStore store, string name)
    {
        var snapshot = await store.GetAsync(fixture.ArchiveRtId)
                       ?? throw new InvalidOperationException("Archive snapshot not available.");
        return snapshot.Columns.Single(c => c.IsComputed && c.Name == name);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!await condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(60))
            {
                throw new TimeoutException("The computed-column backfill did not complete within 60 s.");
            }

            await Task.Delay(250, Ct);
        }
    }
}
