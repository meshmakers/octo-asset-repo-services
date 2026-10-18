using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
///     AB#6968 — the operator path after a restore, end to end against a real CrateDB: the validator reports a seeded
///     inconsistency, a repair is requested (nothing runs in the request), the host tick runs it, the request ends
///     <c>Done</c>, the validator says ok and the values a reader sees equal the values before the damage.
///     One test per kind of damage the validator knows (checks a, b and d; check c needs a MongoDB watermark newer than the
///     CrateDB state and is covered by the engine's own tests).
/// </summary>
[Collection(StreamDataCollection.Name)]
public class RollupRepairEndToEndTests(StreamDataFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task OrphansAboveThePointer_AreFoundRepairedAndTheValuesStayTheSame()
    {
        fixture.OutputHelper = output;
        var kit = await RollupTestKit.CreateAsync(fixture);
        var rollup = await kit.CreateRollupAsync("RepairE2eOrphans");
        await kit.RecomputeAsync(rollup);
        var reference = await kit.ReadActiveValuesAsync(rollup);
        reference.Should().HaveCount(20, "the fixture's raw archive holds 20 points with distinct rtIds");

        // Seed the damage: rows of a generation above the one the pointer selects (a restore of a table whose
        // recompute was not finished), while no recompute runs.
        await kit.ExecuteAsync(
            $"INSERT INTO {rollup.Table} (\"window_start\",\"window_end\",\"rtid\",\"cktypeid\",\"rtwellknownname\"," +
            $"\"was_updated\",\"voltage_sum\",\"generation\") SELECT \"window_start\",\"window_end\",\"rtid\"," +
            $"\"cktypeid\",\"rtwellknownname\",\"was_updated\",\"voltage_sum\",7 FROM {rollup.Table} " +
            "WHERE \"generation\" = 1 LIMIT 3");
        await kit.RefreshAsync(rollup.Table);

        await AssertRepairedAsync(kit, rollup, RollupConsistencyCheck.OrphansAbovePointer, reference);

        (await kit.DistinctGenerationsAsync(rollup.Table)).Should().NotContain(7L, "the orphans are removed");
    }

    [Fact]
    public async Task APointerWithoutRows_IsFoundRepairedByARecomputeAndTheValuesStayTheSame()
    {
        fixture.OutputHelper = output;
        var kit = await RollupTestKit.CreateAsync(fixture);
        var rollup = await kit.CreateRollupAsync("RepairE2ePointer");
        await kit.RecomputeAsync(rollup);
        var reference = await kit.ReadActiveValuesAsync(rollup);

        // Seed the damage: the pointer names generation 1, but its rows are gone (a dump whose data was copied after
        // the sweep of the generation the copied map still names).
        await kit.ExecuteAsync($"DELETE FROM {rollup.Table} WHERE \"generation\" = 1");
        await kit.RefreshAsync(rollup.Table);
        (await kit.ReadActiveValuesAsync(rollup)).Should().BeEmpty("every window is empty before the repair");

        await AssertRepairedAsync(kit, rollup, RollupConsistencyCheck.PointerWithoutRows, reference);
    }

    /// <summary>
    ///     The case the check exists for: a rollup of generation 0 (written before the generation map existed, e.g. a format 1
    ///     backup) without the map table. The repair creates the empty map; generation 0 is what a reader selects where no
    ///     pointer entry exists, so the values stay the same.
    /// </summary>
    [Fact]
    public async Task AMissingGenerationMapTable_OfAGenerationZeroRollup_IsFoundAndCreated()
    {
        fixture.OutputHelper = output;
        var kit = await RollupTestKit.CreateAsync(fixture);
        var rollup = await kit.CreateRollupAsync("RepairE2eNoMap");
        await kit.RecomputeAsync(rollup);
        var reference = await kit.ReadActiveValuesAsync(rollup);

        // Turn the recomputed rows into generation-0 rows (the generation is part of the primary key: copy, then delete).
        await kit.ExecuteAsync(
            $"INSERT INTO {rollup.Table} (\"window_start\",\"window_end\",\"rtid\",\"cktypeid\",\"rtwellknownname\"," +
            $"\"was_updated\",\"voltage_sum\",\"generation\") SELECT \"window_start\",\"window_end\",\"rtid\"," +
            $"\"cktypeid\",\"rtwellknownname\",\"was_updated\",\"voltage_sum\",0 FROM {rollup.Table} " +
            "WHERE \"generation\" = 1");
        await kit.ExecuteAsync($"DELETE FROM {rollup.Table} WHERE \"generation\" = 1");
        await kit.RefreshAsync(rollup.Table);
        await kit.ExecuteAsync($"DROP TABLE {rollup.GenMapTable}");

        await AssertRepairedAsync(kit, rollup, RollupConsistencyCheck.MissingGenerationMap, reference);
    }

    /// <summary>
    ///     A missing generation map table of a rollup whose rows are generation 1 or higher (found by this test, fixed in the
    ///     engine): the pointer entries are gone with the table and cannot be rebuilt from the rows, so the repair creates the
    ///     map and recomputes the whole extent from the sources — one run, validator ok, values equal.
    /// </summary>
    [Fact]
    public async Task AMissingGenerationMapTable_OfARollupWithRecomputedRows_IsRepairedInOneRun()
    {
        fixture.OutputHelper = output;
        var kit = await RollupTestKit.CreateAsync(fixture);
        var rollup = await kit.CreateRollupAsync("RepairE2eNoMapGen1");
        await kit.RecomputeAsync(rollup);
        var reference = await kit.ReadActiveValuesAsync(rollup);

        await kit.ExecuteAsync($"DROP TABLE {rollup.GenMapTable}");

        await AssertRepairedAsync(kit, rollup, RollupConsistencyCheck.MissingGenerationMap, reference);
    }

    [Fact]
    public async Task ARepairedRollup_NeedsNoSecondRepair_AndASecondRequestPlansNothing()
    {
        fixture.OutputHelper = output;
        var kit = await RollupTestKit.CreateAsync(fixture);
        var rollup = await kit.CreateRollupAsync("RepairE2eIdempotent");
        await kit.RecomputeAsync(rollup);
        await kit.ExecuteAsync($"DELETE FROM {rollup.Table} WHERE \"generation\" = 1");
        await kit.RefreshAsync(rollup.Table);

        await RunRepairAsync(kit, rollup);

        var second = await kit.Repository.RepairRollupsAsync([rollup.RtId], true, TestContext.Current.CancellationToken);
        second.IsOk.Should().BeTrue();
        second.Actions.Should().BeEmpty("a second run on a repaired rollup finds nothing to do (idempotent)");
    }

    private async Task AssertRepairedAsync(RollupTestKit kit, RollupHandle rollup, RollupConsistencyCheck expectedCheck,
        List<double> reference)
    {
        var before = await kit.Repository.ValidateRollupsAsync([rollup.RtId], TestContext.Current.CancellationToken);
        before.IsOk.Should().BeFalse();
        var entry = before.Rollups.Should().ContainSingle(r => r.RollupRtId == rollup.RtId).Subject;
        entry.Findings.Select(f => f.Check).Should().Contain(expectedCheck);

        var finished = await RunRepairAsync(kit, rollup);

        finished.State.Should().Be(RollupRepairRequestState.Done,
            $"the repair ended with: {finished.Report?.FailedStep ?? "no failed step reported"}");
        finished.Report.Should().NotBeNull();
        finished.Report!.FailedStep.Should().BeNull();
        finished.Report.IsOk.Should().BeTrue();

        var after = await kit.Repository.ValidateRollupsAsync([rollup.RtId], TestContext.Current.CancellationToken);
        after.IsOk.Should().BeTrue("the validator says ok after the repair");

        (await kit.ReadActiveValuesAsync(rollup)).Should().Equal(reference,
            "the values a reader sees equal the values before the damage, with no empty window");
    }

    /// <summary>Requests the repair, runs the host tick and returns the finished request.</summary>
    private async Task<RollupRepairRequest> RunRepairAsync(RollupTestKit kit, RollupHandle rollup)
    {
        var request = await kit.Repository.EnqueueRollupRepairAsync([rollup.RtId], TestContext.Current.CancellationToken);
        request.State.Should().Be(RollupRepairRequestState.Pending, "the request itself runs nothing");
        request.PlannedActions.Should().NotBeEmpty();

        // The host's recompute tick executes pending repairs; a test calls it directly.
        var ran = await kit.Repository.RunPendingRollupRepairsAsync(TestContext.Current.CancellationToken);
        ran.Should().BeGreaterThan(0);

        var finished = await kit.Repository.GetRollupRepairRequestAsync(request.RequestId, TestContext.Current.CancellationToken);
        finished.Should().NotBeNull();
        output.WriteLine($"Repair request {finished!.RequestId}: {finished.State}, " +
                         $"{finished.Report?.Actions.Count} step(s)");
        return finished;
    }
}
