using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
///     AB#8599: rollup control and recomputation as methods of RollupArchive (System.StreamData 1.25.0). Each method runs side by
///     side with the streamData mutation it replaces on two rollups of the same source and must leave the same state; without
///     StreamDataAdmin every method is METHOD_FORBIDDEN and changes nothing.
/// </summary>
public partial class ArchiveLifecycleMethodTests
{
    private static readonly DateTime Until = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Freeze_ThenUnfreeze_ChangeFrozenUntilLikeTheMutations()
    {
        var (viaMutation, viaMethod) = await CreateRollupPairAsync("Freeze");

        var mutation = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { streamData { freezeRollupArchive(rtId: "{{viaMutation}}", until: "{{Until:O}}") { archiveRtId status transition } } }
            """, user: Admin());
        var method = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{Rollup}} { freeze(rtId: "{{viaMethod}}", input: { until: "{{Until:O}}" }) { result { archiveRtId status transition } } } } }
            """, user: Admin());

        AssertTransitionResult(mutation, viaMutation, "Freeze", "CREATED");
        AssertTransitionResult(method, viaMethod, "Freeze", "CREATED");
        (await RollupAsync(viaMethod)).FrozenUntil.Should().Be((await RollupAsync(viaMutation)).FrozenUntil).And.Be(Until);

        // monotonic: an earlier freeze is the mutation's invalid transition
        var earlier = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{Rollup}} { freeze(rtId: "{{viaMethod}}", input: { until: "{{Until.AddDays(-1):O}}" }) { invocationId } } } }
            """, user: Admin());
        earlier.Errors.Should().ContainSingle().Which.Code.Should().Be(Statics.GraphQlErrorStreamDataInvalidTransition);

        await _fixture.ExecuteGraphQlAsync($$"""
            mutation { streamData { unfreezeRollupArchive(rtId: "{{viaMutation}}") { transition } } }
            """, user: Admin());
        var unfreeze = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{Rollup}} { unfreeze(rtId: "{{viaMethod}}", input: { acceptGaps: true }) { result { archiveRtId status transition } } } } }
            """, user: Admin());

        AssertTransitionResult(unfreeze, viaMethod, "Unfreeze", "CREATED");
        (await RollupAsync(viaMethod)).FrozenUntil.Should().BeNull();
        (await RollupAsync(viaMutation)).FrozenUntil.Should().BeNull();
    }

    [Fact]
    public async Task RewindWatermark_SetsTheWatermarkLikeTheMutation()
    {
        var (viaMutation, viaMethod) = await CreateRollupPairAsync("Rewind");
        // inside the bucket: both paths align it down to the bucket boundary
        var target = Until.AddMinutes(17);

        var mutation = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { streamData { rewindRollupWatermark(rtId: "{{viaMutation}}", toBucketEnd: "{{target:O}}") { archiveRtId status transition } } }
            """, user: Admin());
        var method = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{Rollup}} { rewindWatermark(rtId: "{{viaMethod}}", input: { toBucketEnd: "{{target:O}}" }) { result { archiveRtId status transition } } } } }
            """, user: Admin());

        AssertTransitionResult(mutation, viaMutation, "RewindWatermark", "CREATED");
        AssertTransitionResult(method, viaMethod, "RewindWatermark", "CREATED");
        (await RollupAsync(viaMethod)).LastAggregatedBucketEnd.Should()
            .Be((await RollupAsync(viaMutation)).LastAggregatedBucketEnd).And.Be(Until);
    }

    [Fact]
    public async Task Recompute_AnswersTheJobLikeRecomputeArchive_AndAnInactiveRollupIsAFailedJob()
    {
        var (viaMutation, viaMethod) = await CreateRollupPairAsync("Recompute", activate: true);
        const string jobFields = "rtId state rowsProcessed windowsProcessed errorReason";
        const string methodJobFields = "jobRtId state rowsProcessed windowsProcessed errorReason";

        var mutation = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { streamData { recomputeArchive(rtId: "{{viaMutation}}", from: "{{Until:O}}", to: "{{Until.AddDays(1):O}}") { {{jobFields}} } } }
            """, user: Admin());
        var method = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{Rollup}} { recompute(rtId: "{{viaMethod}}", input: { from: "{{Until:O}}", to: "{{Until.AddDays(1):O}}" }) { result { {{methodJobFields}} } } } } }
            """, user: Admin());

        var expected = Data(mutation).SelectToken("streamData.recomputeArchive")!;
        var actual = Data(method).SelectToken($"runtime.{Rollup}.recompute.result")!;
        Normalize(actual.Value<string>("state")!).Should().Be(Normalize(expected.Value<string>("state")!));
        actual.Value<int?>("windowsProcessed").Should().Be(expected.Value<int?>("windowsProcessed"));
        actual.Value<string>("errorReason").Should().Be(expected.Value<string>("errorReason"));
        var job = await (await TenantAsync()).GetRecomputeJobStore()!.GetAsync(new OctoObjectId(actual.Value<string>("jobRtId")!));
        job.Should().NotBeNull("the method answers the persisted RecomputeJob");
        job!.ArchiveRtId.Should().Be(viaMethod);
        job.Trigger.Should().Be(RecomputeTrigger.Manual);

        // a rollup that is not activated: the mutation's fail-fast job, state Failed with the reason
        var (_, inactive) = await CreateRollupPairAsync("RecomputeInactive");
        var failed = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{Rollup}} { recompute(rtId: "{{inactive}}", input: { from: "{{Until:O}}", to: "{{Until.AddDays(1):O}}" }) { result { {{methodJobFields}} } } } } }
            """, user: Admin());
        var failedJob = Data(failed).SelectToken($"runtime.{Rollup}.recompute.result")!;
        Normalize(failedJob.Value<string>("state")!).Should().Be("FAILED");
        failedJob.Value<string>("errorReason").Should().Contain("not activated");

        var invalidScope = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{Rollup}} { recompute(rtId: "{{viaMethod}}", input: { from: "{{Until:O}}", to: "{{Until.AddDays(1):O}}", rtIdScope: "nope" }) { invocationId } } } }
            """, user: Admin());
        invalidScope.Errors.Should().ContainSingle().Which.Code.Should().Be(CkMethodErrorCodes.MethodValidationFailed);
    }

    [Fact]
    public async Task BackfillFromSource_OfAnEmptySource_AnswersNoJob_LikeTheMutation()
    {
        var (viaMutation, viaMethod) = await CreateRollupPairAsync("Backfill", activate: true);

        var mutation = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { streamData { backfillRollupFromSource(rtId: "{{viaMutation}}") { rtId state } } }
            """, user: Admin());
        var method = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{Rollup}} { backfillFromSource(rtId: "{{viaMethod}}") { invocationId result { jobRtId state } } } } }
            """, user: Admin());

        Data(mutation).SelectToken("streamData.backfillRollupFromSource")!.Type.Should().Be(Newtonsoft.Json.Linq.JTokenType.Null);
        Data(method).SelectToken($"runtime.{Rollup}.backfillFromSource.result")!.Type.Should()
            .Be(Newtonsoft.Json.Linq.JTokenType.Null, "an empty source archive has nothing to backfill");
    }

    [Fact]
    public async Task EveryRollupControlMethod_WithoutStreamDataAdmin_IsMethodForbidden_AndChangesNothing()
    {
        var (_, rollup) = await CreateRollupPairAsync("RollupForbidden", activate: true);
        var calls = new[]
        {
            $$"""{{Rollup}} { freeze(rtId: "{{rollup}}", input: { until: "{{Until:O}}" }) { invocationId } }""",
            $$"""{{Rollup}} { unfreeze(rtId: "{{rollup}}") { invocationId } }""",
            $$"""{{Rollup}} { rewindWatermark(rtId: "{{rollup}}", input: { toBucketEnd: "{{Until:O}}" }) { invocationId } }""",
            $$"""{{Rollup}} { recompute(rtId: "{{rollup}}", input: { from: "{{Until:O}}", to: "{{Until.AddDays(1):O}}" }) { invocationId } }""",
            $$"""{{Rollup}} { backfillFromSource(rtId: "{{rollup}}") { invocationId } }"""
        };

        foreach (var call in calls)
        {
            var result = await _fixture.ExecuteGraphQlAsync($"mutation {{ runtime {{ {call} }} }}", user: WithoutRole());
            result.Errors.Should().ContainSingle(call).Which.Code.Should().Be(CkMethodErrorCodes.MethodForbidden, call);
        }

        var snapshot = await RollupAsync(rollup);
        snapshot.FrozenUntil.Should().BeNull();
        snapshot.LastAggregatedBucketEnd.Should().NotBe(Until);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Two hourly rollups of one raw source: one for the mutation, one for the method.</summary>
    private async Task<(OctoObjectId ViaMutation, OctoObjectId ViaMethod)> CreateRollupPairAsync(string prefix,
        bool activate = false)
    {
        var source = await CreateRawArchiveAsync($"{prefix}Src", activate: true);
        var tenant = await TenantAsync();
        var lifecycle = tenant.GetRollupArchiveLifecycleService()!;
        var ids = new List<OctoObjectId>();
        foreach (var suffix in new[] { "Mut", "Meth" })
        {
            var rtId = await lifecycle.CreateAsync($"{prefix}{suffix}{Guid.NewGuid():N}",
                [new RollupSourceReference(source)], TimeSpan.FromHours(1), TimeSpan.Zero,
                [new CkRollupAggregationSpec("Voltage", CkRollupFunction.Sum, "voltageSum", null)],
                BucketAlignment.FixedSize, null, null, null);
            if (activate)
            {
                await tenant.GetArchiveLifecycleService()!.ActivateAsync(rtId);
            }

            ids.Add(rtId);
        }

        return (ids[0], ids[1]);
    }

    private async Task<RollupArchiveSnapshot> RollupAsync(OctoObjectId rtId) =>
        (await (await TenantAsync()).GetRollupArchiveRuntimeStore()!.GetAsync(rtId))!;
}
