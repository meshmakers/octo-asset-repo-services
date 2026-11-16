using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Authorization;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
///     AB#8600: archive columns and computed columns as methods of Archive (System.StreamData 1.28.0). Each method runs side by
///     side with the streamData mutation it replaces, on two archives, and must leave the same archive definition and answer
///     the same result or error code; a path that reaches a Hidden attribute is refused as the mutation refuses it (AB#6247);
///     without StreamDataAdmin every method is METHOD_FORBIDDEN and changes nothing.
/// </summary>
public partial class ArchiveLifecycleMethodTests
{
    private const string AccessTestAccount = "AssetRepositoryIntegrationTest/AccessTestAccount";

    [Fact]
    public async Task AddComputedColumn_AddsTheColumnLikeAddComputedColumn()
    {
        var viaMutation = await CreateRawArchiveAsync("AddCcMut", activate: true);
        var viaMethod = await CreateRawArchiveAsync("AddCcMeth", activate: true);

        var mutation = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { streamData { addComputedColumn(rtId: "{{viaMutation}}", name: "doubled", formula: "Voltage * 2", resultType: DOUBLE, indexed: false) { archiveRtId status transition } } }
            """, user: Admin());
        var method = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{Raw}} { addComputedColumn(rtId: "{{viaMethod}}", input: { name: "doubled", formula: "Voltage * 2", resultType: DOUBLE, indexed: false }) { result { archiveRtId status transition } } } } }
            """, user: Admin());

        AssertTransitionResult(mutation, viaMutation, "AddComputedColumn", "ACTIVATED");
        AssertTransitionResult(method, viaMethod, "AddComputedColumn", "ACTIVATED");
        var expected = await ColumnAsync(viaMutation, "doubled");
        var actual = await ColumnAsync(viaMethod, "doubled");
        (actual.Formula, actual.ResultType, actual.Indexed).Should().Be((expected.Formula, expected.ResultType, expected.Indexed));
        actual.ResultType.Should().Be(FormulaResultType.Double);
        actual.ComputedState.Should().BeOneOf(ComputedColumnState.Backfilling, ComputedColumnState.Active);
    }

    [Fact]
    public async Task AddColumn_AddsTheIngestedColumnLikeAddArchiveColumn_AndIsIdempotent()
    {
        var viaMutation = await CreateRawArchiveAsync("AddColMut", activate: true);
        var viaMethod = await CreateRawArchiveAsync("AddColMeth", activate: true);

        var mutation = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { streamData { addArchiveColumn(rtId: "{{viaMutation}}", path: "Current", indexed: true) { archiveRtId status transition } } }
            """, user: Admin());
        var method = await AddColumnAsync(viaMethod, "Current", indexed: true);

        AssertTransitionResult(mutation, viaMutation, "AddArchiveColumn", "ACTIVATED");
        AssertTransitionResult(method, viaMethod, "AddArchiveColumn", "ACTIVATED");
        (await SnapshotAsync(viaMethod))!.Columns.Should().BeEquivalentTo((await SnapshotAsync(viaMutation))!.Columns);

        // the same path again changes nothing, as with the mutation
        AssertTransitionResult(await AddColumnAsync(viaMethod, "Current", indexed: true), viaMethod, "AddArchiveColumn",
            "ACTIVATED");
        (await SnapshotAsync(viaMethod))!.Columns.Select(c => c.Path).Should().Equal("Voltage", "Current");
    }

    [Fact]
    public async Task AddColumn_OfAHiddenAttribute_IsRefusedLikeAddArchiveColumn_AndChangesNothing()
    {
        var viaMutation = await CreateArchiveOfAsync(AccessTestAccount, "Name", "HiddenMut");
        var viaMethod = await CreateArchiveOfAsync(AccessTestAccount, "Name", "HiddenMeth");

        var mutation = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { streamData { addArchiveColumn(rtId: "{{viaMutation}}", path: "PasswordHash") { archiveRtId } } }
            """, user: Admin());
        var method = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { systemStreamDataRawArchives { addColumn(rtId: "{{viaMethod}}", input: { path: "PasswordHash" }) { invocationId } } } }
            """, user: Admin());

        var mutationError = mutation.Errors!.First();
        mutationError.Code.Should().Be(Statics.GraphQlErrorStreamData);
        method.Errors.Should().ContainSingle().Which.Code.Should().Be(mutationError.Code, "the method answers the mutation's code");
        MethodErrorParameters(method).Value<string>("message").Should().Contain("Hidden attribute 'PasswordHash'");
        (await SnapshotAsync(viaMethod))!.Columns.Select(c => c.Path).Should().Equal("Name");
        (await SnapshotAsync(viaMutation))!.Columns.Select(c => c.Path).Should().Equal("Name");
    }

    [Fact]
    public async Task AddColumn_OnARollup_IsRefusedLikeAddArchiveColumn()
    {
        var (viaMutation, viaMethod) = await CreateRollupPairAsync("RollupAddCol", activate: true);

        var mutation = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { streamData { addArchiveColumn(rtId: "{{viaMutation}}", path: "Current") { archiveRtId } } }
            """, user: Admin());
        var method = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{Rollup}} { addColumn(rtId: "{{viaMethod}}", input: { path: "Current" }) { invocationId } } } }
            """, user: Admin());

        var mutationError = mutation.Errors!.First();
        mutationError.Code.Should().Be(Statics.GraphQlErrorStreamDataPathInvalid);
        method.Errors.Should().ContainSingle().Which.Code.Should().Be(mutationError.Code, "the method answers the mutation's code");
        (await SnapshotAsync(viaMethod))!.Columns.Should().BeEquivalentTo((await SnapshotAsync(viaMutation))!.Columns,
            "the rollup's columns stay as derived");
        (await SnapshotAsync(viaMethod))!.Columns.Should().NotContain(c => c.Path == "Current");
    }

    /// <summary>
    ///     An unknown computed column: the engine refuses update, retry and repair with a plain error, which the mutations answer
    ///     with their generic code. The methods answer the declared STREAMDATA_ERROR with the engine message instead of an
    ///     undeclared failure. Removing an unknown column changes nothing, for the mutation and the method alike.
    /// </summary>
    [Fact]
    public async Task AnUnknownComputedColumn_IsRefusedWithStreamDataError_AndItsRemovalChangesNothing()
    {
        var archive = await CreateRawArchiveAsync("UnknownCol", activate: true);
        var calls = new[]
        {
            $$"""updateComputedColumnFormula(rtId: "{{archive}}", input: { name: "nope", formula: "Voltage" }) { invocationId }""",
            $$"""retryComputedColumnBackfill(rtId: "{{archive}}", input: { name: "nope" }) { invocationId }""",
            $$"""repairComputedColumn(rtId: "{{archive}}", input: { name: "nope" }) { invocationId }"""
        };

        foreach (var call in calls)
        {
            var result = await _fixture.ExecuteGraphQlAsync($"mutation {{ runtime {{ {Raw} {{ {call} }} }} }}", user: Admin());
            result.Errors.Should().ContainSingle(call).Which.Code.Should().Be(Statics.GraphQlErrorStreamData, call);
            MethodErrorParameters(result).Value<string>("message").Should().Contain("nope", call);
        }

        var mutation = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { streamData { updateComputedColumnFormula(rtId: "{{archive}}", name: "nope", formula: "Voltage") { archiveRtId } } }
            """, user: Admin());
        mutation.Errors.Should().NotBeNullOrEmpty("the mutation refuses it too, with its generic code");

        var removeViaMutation = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { streamData { removeComputedColumn(rtId: "{{archive}}", name: "nope") { archiveRtId status transition } } }
            """, user: Admin());
        var removeViaMethod = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{Raw}} { removeComputedColumn(rtId: "{{archive}}", input: { name: "nope" }) { result { archiveRtId status transition } } } } }
            """, user: Admin());
        AssertTransitionResult(removeViaMutation, archive, "RemoveComputedColumn", "ACTIVATED");
        AssertTransitionResult(removeViaMethod, archive, "RemoveComputedColumn", "ACTIVATED");
        (await SnapshotAsync(archive))!.Columns.Select(c => c.Path).Should().Equal("Voltage");
    }

    [Fact]
    public async Task UpdateComputedColumnFormula_QueuesTheNewFormulaLikeTheMutation()
    {
        var viaMutation = await CreateArchiveWithActiveComputedColumnAsync("UpdMut", "tripled");
        var viaMethod = await CreateArchiveWithActiveComputedColumnAsync("UpdMeth", "tripled");

        var mutation = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { streamData { updateComputedColumnFormula(rtId: "{{viaMutation}}", name: "tripled", formula: "Voltage * 3") { archiveRtId status transition } } }
            """, user: Admin());
        var method = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{Raw}} { updateComputedColumnFormula(rtId: "{{viaMethod}}", input: { name: "tripled", formula: "Voltage * 3" }) { result { archiveRtId status transition } } } } }
            """, user: Admin());

        AssertTransitionResult(mutation, viaMutation, "UpdateComputedColumnFormula", "ACTIVATED");
        AssertTransitionResult(method, viaMethod, "UpdateComputedColumnFormula", "ACTIVATED");
        var expected = await ColumnAsync(viaMutation, "tripled");
        var actual = await ColumnAsync(viaMethod, "tripled");
        // the backfill may already have swapped the version; either way both columns end in the same place
        // (the engine stores the formula with normalised identifiers, for both paths alike)
        (actual.PendingFormula ?? actual.Formula).Should().Be(expected.PendingFormula ?? expected.Formula)
            .And.BeEquivalentTo("Voltage * 3");
    }

    [Fact]
    public async Task RemoveComputedColumn_RemovesTheColumnLikeTheMutation()
    {
        var viaMutation = await CreateArchiveWithActiveComputedColumnAsync("RemMut", "gone");
        var viaMethod = await CreateArchiveWithActiveComputedColumnAsync("RemMeth", "gone");

        var mutation = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { streamData { removeComputedColumn(rtId: "{{viaMutation}}", name: "gone") { archiveRtId status transition } } }
            """, user: Admin());
        var method = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{Raw}} { removeComputedColumn(rtId: "{{viaMethod}}", input: { name: "gone" }) { result { archiveRtId status transition } } } } }
            """, user: Admin());

        AssertTransitionResult(mutation, viaMutation, "RemoveComputedColumn", "ACTIVATED");
        AssertTransitionResult(method, viaMethod, "RemoveComputedColumn", "ACTIVATED");
        (await SnapshotAsync(viaMethod))!.Columns.Should().NotContain(c => c.Name == "gone");
        (await SnapshotAsync(viaMutation))!.Columns.Should().NotContain(c => c.Name == "gone");
    }

    [Fact]
    public async Task RetryComputedColumnBackfill_OfAnActiveColumn_IsRefusedLikeTheMutation()
    {
        var archive = await CreateArchiveWithActiveComputedColumnAsync("RetryActive", "settled");

        var mutation = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { streamData { retryComputedColumnBackfill(rtId: "{{archive}}", name: "settled") { rtId } } }
            """, user: Admin());
        var method = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{Raw}} { retryComputedColumnBackfill(rtId: "{{archive}}", input: { name: "settled" }) { invocationId } } } }
            """, user: Admin());

        var mutationError = mutation.Errors.Should().ContainSingle().Subject;
        method.Errors.Should().ContainSingle().Which.Code.Should().Be(mutationError.Code, "the method answers the mutation's code");
        (await ColumnAsync(archive, "settled")).ComputedState.Should().Be(ComputedColumnState.Active);
    }

    [Fact]
    public async Task RetryComputedColumnBackfill_OfAFailedColumn_QueuesTheBackfillLikeTheMutation()
    {
        var viaMutation = await CreateArchiveWithFailedComputedColumnAsync("RetryMut", "broken");
        var viaMethod = await CreateArchiveWithFailedComputedColumnAsync("RetryMeth", "broken");

        var mutation = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { streamData { retryComputedColumnBackfill(rtId: "{{viaMutation}}", name: "broken") { rtId state computedColumnName } } }
            """, user: Admin());
        var method = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{Raw}} { retryComputedColumnBackfill(rtId: "{{viaMethod}}", input: { name: "broken" }) { result { jobRtId state computedColumnName } } } } }
            """, user: Admin());

        Data(mutation).SelectToken("streamData.retryComputedColumnBackfill.computedColumnName")!.Value<string>()
            .Should().Be("broken");
        var job = Data(method).SelectToken($"runtime.{Raw}.retryComputedColumnBackfill.result")!;
        job.Value<string>("computedColumnName").Should().Be("broken");
        job.Value<string>("jobRtId").Should().NotBeNullOrEmpty();
        (await ColumnAsync(viaMethod, "broken")).ComputedState.Should()
            .Be((await ColumnAsync(viaMutation, "broken")).ComputedState)
            .And.BeOneOf(ComputedColumnState.Backfilling, ComputedColumnState.Active);
    }

    [Fact]
    public async Task RepairComputedColumn_QueuesTheRepairLikeTheMutation_AndAnswersAQueuedRepairAsIs()
    {
        var viaMutation = await CreateArchiveWithActiveComputedColumnAsync("RepMut", "fixed");
        var viaMethod = await CreateArchiveWithActiveComputedColumnAsync("RepMeth", "fixed");
        var from = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var mutation = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { streamData { repairComputedColumn(rtId: "{{viaMutation}}", name: "fixed", from: "{{from:O}}") { rtId computedColumnName } } }
            """, user: Admin());
        var method = await RepairAsync(viaMethod, "fixed", from);

        Data(mutation).SelectToken("streamData.repairComputedColumn.computedColumnName")!.Value<string>().Should().Be("fixed");
        var job = Data(method).SelectToken($"runtime.{Raw}.repairComputedColumn.result")!;
        job.Value<string>("computedColumnName").Should().Be("fixed");
        (await ColumnAsync(viaMethod, "fixed")).ComputedState.Should().Be(ComputedColumnState.Active, "a repair keeps the column readable");

        var jobs = (await TenantAsync()).GetRecomputeJobStore();
        var queued = await jobs.GetAsync(new OctoObjectId(job.Value<string>("jobRtId")!));
        queued.Should().NotBeNull();
        queued!.IsComputedColumnRepair.Should().BeTrue();
        if (queued.State is RecomputeJobState.Pending or RecomputeJobState.Running)
        {
            // still queued: a second call answers that job as is, as the mutation does
            var again = Data(await RepairAsync(viaMethod, "fixed", from)).SelectToken($"runtime.{Raw}.repairComputedColumn.result")!;
            again.Value<string>("jobRtId").Should().Be(job.Value<string>("jobRtId"));
        }
    }

    [Fact]
    public async Task EveryColumnMethod_WithoutStreamDataAdmin_IsMethodForbidden_AndChangesNothing()
    {
        var archive = await CreateArchiveWithActiveComputedColumnAsync("ColForbidden", "kept");
        var before = (await SnapshotAsync(archive))!.Columns;
        var calls = new[]
        {
            $$"""addComputedColumn(rtId: "{{archive}}", input: { name: "x", formula: "Voltage", resultType: DOUBLE }) { invocationId }""",
            $$"""addColumn(rtId: "{{archive}}", input: { path: "Current" }) { invocationId }""",
            $$"""removeComputedColumn(rtId: "{{archive}}", input: { name: "kept" }) { invocationId }""",
            $$"""updateComputedColumnFormula(rtId: "{{archive}}", input: { name: "kept", formula: "Voltage * 9" }) { invocationId }""",
            $$"""retryComputedColumnBackfill(rtId: "{{archive}}", input: { name: "kept" }) { invocationId }""",
            $$"""repairComputedColumn(rtId: "{{archive}}", input: { name: "kept" }) { invocationId }"""
        };

        foreach (var call in calls)
        {
            var result = await _fixture.ExecuteGraphQlAsync($"mutation {{ runtime {{ {Raw} {{ {call} }} }} }}", user: WithoutRole());
            result.Errors.Should().ContainSingle(call).Which.Code.Should().Be(CkMethodErrorCodes.MethodForbidden, call);
        }

        (await SnapshotAsync(archive))!.Columns.Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task AColumnMethod_WithATokenOfAnotherTenant_IsMethodForbidden_AndChangesNothing()
    {
        var archive = await CreateRawArchiveAsync("ColOtherTenant", activate: true);
        var foreignAdmin = UserOf("some-other-tenant", CommonConstants.StreamDataAdminRole);

        var result = await _fixture.ExecuteGraphQlAsync(
            $$"""mutation { runtime { {{Raw}} { addColumn(rtId: "{{archive}}", input: { path: "Current" }) { invocationId } } } }""",
            user: foreignAdmin);

        result.Errors.Should().ContainSingle().Which.Code.Should().Be(CkMethodErrorCodes.MethodForbidden);
        MethodErrorParameters(result).Value<string>("reason").Should().Be(CkMethodTokenReasons.TenantNotAllowed);
        (await SnapshotAsync(archive))!.Columns.Select(c => c.Path).Should().Equal("Voltage");
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private async Task<ExecutionResult> AddColumnAsync(OctoObjectId rtId, string path, bool indexed) =>
        await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{Raw}} { addColumn(rtId: "{{rtId}}", input: { path: "{{path}}", indexed: {{(indexed ? "true" : "false")}} }) { result { archiveRtId status transition } } } } }
            """, user: Admin());

    private async Task<ExecutionResult> RepairAsync(OctoObjectId rtId, string name, DateTime from) =>
        await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{Raw}} { repairComputedColumn(rtId: "{{rtId}}", input: { name: "{{name}}", from: "{{from:O}}" }) { result { jobRtId state computedColumnName } } } } }
            """, user: Admin());

    private async Task<CkArchiveColumnSpec> ColumnAsync(OctoObjectId rtId, string name) =>
        (await SnapshotAsync(rtId))!.Columns.Single(c => c.Name == name);

    /// <summary>An activated raw archive with a computed column that the backfill drain has made Active.</summary>
    private async Task<OctoObjectId> CreateArchiveWithActiveComputedColumnAsync(string prefix, string column)
    {
        var archive = await CreateRawArchiveAsync(prefix, activate: true);
        var lifecycle = (await TenantAsync()).GetArchiveLifecycleService()!;
        await lifecycle.EnqueueAddComputedColumnAsync(archive, column, "Voltage * 2", FormulaResultType.Double, indexed: false);
        await DrainUntilAsync(archive, column, ComputedColumnState.Active);
        return archive;
    }

    /// <summary>An activated raw archive with a computed column whose backfill ended Failed (state written as the drain does).</summary>
    private async Task<OctoObjectId> CreateArchiveWithFailedComputedColumnAsync(string prefix, string column)
    {
        var archive = await CreateArchiveWithActiveComputedColumnAsync(prefix, column);
        await (await TenantAsync()).GetArchiveRuntimeStore()
            .SetComputedColumnStateAsync(archive, column, ComputedColumnState.Failed);
        (await ColumnAsync(archive, column)).ComputedState.Should().Be(ComputedColumnState.Failed);
        return archive;
    }

    private async Task DrainUntilAsync(OctoObjectId archive, string column, ComputedColumnState state)
    {
        var lifecycle = (await TenantAsync()).GetArchiveLifecycleService()!;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await lifecycle.RunPendingComputedColumnBackfillsAsync(TestContext.Current.CancellationToken);
            if ((await ColumnAsync(archive, column)).ComputedState == state)
            {
                return;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        (await ColumnAsync(archive, column)).ComputedState.Should().Be(state, "the backfill drain should have settled the column");
    }

    /// <summary>An activated raw archive of another CK type of the test model with one ingested column.</summary>
    private async Task<OctoObjectId> CreateArchiveOfAsync(string ckTypeId, string path, string namePrefix)
    {
        var archive = new RtRawArchive
        {
            RtWellKnownName = $"{namePrefix}{Guid.NewGuid():N}",
            TargetCkTypeId = ckTypeId,
            Status = RtCkArchiveStatusEnum.Created,
            Columns = new AttributeRecordValueList<RtCkArchiveColumnRecord>
            {
                new() { Path = path, Indexed = false, Required = false }
            }
        };
        var repository = _fixture.GetSystemContext().GetSystemTenantRepository();
        using (var session = await repository.GetSessionAsync())
        {
            session.StartTransaction();
            await repository.InsertOneRtEntityAsync(session, archive);
            await session.CommitTransactionAsync();
        }

        await (await TenantAsync()).GetArchiveLifecycleService()!.ActivateAsync(archive.RtId);
        return archive.RtId;
    }
}
