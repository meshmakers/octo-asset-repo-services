using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Methods;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods;
using Meshmakers.Octo.Services.Methods.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AssetRepositoryServices.UnitTests.StreamData.Methods;

/// <summary>
///     the asynchronous <c>RollupArchive.Recompute-2</c> and <c>BackfillFromSource-2</c>. The handler accepts at once
///     (no engine call before the accept); the work runs the recompute or follows the backfill job to its end and answers the
///     job, or <c>STREAMDATA_RECOMPUTE_FAILED</c> with the job id for a job that ended Failed. The REST wait reads the job id
///     from the operation record.
/// </summary>
public class RecomputeAsyncHandlerTests
{
    private const string TenantId = "t1";
    private static readonly OctoObjectId RollupId = OctoObjectId.GenerateNewId();
    private static readonly DateTime From = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly ISystemContext _systemContext = A.Fake<ISystemContext>();
    private readonly ITenantContext _tenant = A.Fake<ITenantContext>();
    private readonly IRecomputeOrchestrator _orchestrator = A.Fake<IRecomputeOrchestrator>();
    private readonly IRecomputeJobStore _jobStore = A.Fake<IRecomputeJobStore>();
    private readonly RecordingProgress _progress = new();

    public RecomputeAsyncHandlerTests()
    {
        A.CallTo(() => _systemContext.TryFindTenantContextAsync(TenantId)).Returns(_tenant);
        A.CallTo(() => _tenant.GetRecomputeOrchestrator()).Returns(_orchestrator);
        A.CallTo(() => _tenant.GetRecomputeJobStore()).Returns(_jobStore);
    }

    private static RecomputeJobSnapshot Job(OctoObjectId id, RecomputeJobState state, string? error = null) =>
        new(id, RollupId, state, RecomputeTrigger.Manual, From, From.AddDays(1), null, 10, 2, From, null, null, error, null);

    private static CkMethodContext<TParameters> Call<TParameters>(string methodId, TParameters parameters)
        where TParameters : class =>
        new(new CkMethodRequest(TenantId)
        {
            InvocationId = Guid.NewGuid(), MethodId = methodId, CkTypeId = "System.StreamData/RollupArchive",
            TargetRtId = RollupId.ToString(), DeadlineUtc = DateTime.UtcNow.AddSeconds(30)
        }, parameters);

    private RollupArchiveRecompute2Handler Recompute() => new(_systemContext, NullLogger<RollupArchiveRecompute2Handler>.Instance);

    private RollupArchiveBackfillFromSource2Handler Backfill() => new(_systemContext,
        Options.Create(new StreamDataMethodsOptions { OperationPollInterval = TimeSpan.FromMilliseconds(1) }),
        NullLogger<RollupArchiveBackfillFromSource2Handler>.Instance);

    [Fact]
    public async Task Recompute2_AcceptsAtOnce_WithoutRunningTheRecompute()
    {
        var outcome = await Recompute().HandleAsync(Call(SystemStreamDataCkIds.RollupArchiveRecompute2MethodId,
            new RollupArchiveRecompute2Parameters { From = From, To = From.AddDays(1) }), CancellationToken.None);

        outcome.IsAccepted.Should().BeTrue("the caller gets the operation handle at once");
        A.CallTo(_orchestrator).MustNotHaveHappened();
    }

    [Fact]
    public async Task Recompute2_RefusesAnInvalidScope_AndATenantWithoutStreamData_BeforeTheAccept()
    {
        var invalid = await Recompute().HandleAsync(Call(SystemStreamDataCkIds.RollupArchiveRecompute2MethodId,
            new RollupArchiveRecompute2Parameters { From = From, To = From.AddDays(1), RtIdScope = "nope" }),
            CancellationToken.None);
        invalid.IsAccepted.Should().BeFalse();
        invalid.Failure!.Code.Should().Be(CkMethodErrorCodes.MethodValidationFailed);

        A.CallTo(() => _tenant.GetRecomputeOrchestrator()).Returns(null);
        var disabled = await Recompute().HandleAsync(Call(SystemStreamDataCkIds.RollupArchiveRecompute2MethodId,
            new RollupArchiveRecompute2Parameters { From = From, To = From.AddDays(1) }), CancellationToken.None);
        disabled.IsAccepted.Should().BeFalse();
        disabled.Failure!.Code.Should().Be(ArchiveMethodErrors.StreamDataNotEnabled);
    }

    [Fact]
    public async Task Recompute2Work_AnswersTheCompletedJob_AndReportsTheRunningStage()
    {
        var jobId = OctoObjectId.GenerateNewId();
        A.CallTo(() => _orchestrator.RecomputeArchiveAsync(RollupId, From, From.AddDays(1), null, RecomputeTrigger.Manual,
            A<CancellationToken>._)).Returns(Job(jobId, RecomputeJobState.Completed));

        var outcome = await Recompute().RunAsync(_orchestrator, RollupId, From, From.AddDays(1), null, _progress,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value!.Value!.JobRtId.Should().Be(jobId.ToString());
        outcome.Value.Value.State.Should().Be(RtCkRecomputeJobStateEnum.Completed);
        _progress.Reports.Select(r => r.StageCode).Should().Equal(RecomputeOperations.RecomputingStage);
    }

    [Fact]
    public async Task Recompute2Work_AFailedJob_IsRecomputeFailed_WithTheJobIdAndItsReason()
    {
        var jobId = OctoObjectId.GenerateNewId();
        A.CallTo(() => _orchestrator.RecomputeArchiveAsync(A<OctoObjectId>._, A<DateTime>._, A<DateTime>._,
                A<OctoObjectId?>._, A<RecomputeTrigger>._, A<CancellationToken>._))
            .Returns(Job(jobId, RecomputeJobState.Failed, "rollup is not activated"));

        var outcome = await Recompute().RunAsync(_orchestrator, RollupId, From, From.AddDays(1), null, _progress,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeFalse();
        outcome.Failure!.Code.Should().Be(ArchiveMethodErrors.RecomputeFailed);
        outcome.Failure.Parameters[ArchiveMethodErrors.JobRtIdParameter].Should().Be(jobId.ToString());
        outcome.Failure.Parameters[ArchiveMethodErrors.ReasonParameter].Should().Be("rollup is not activated");
    }

    [Fact]
    public async Task Recompute2Work_AStreamDataRefusal_KeepsTheCodeOfTheMutation()
    {
        A.CallTo(() => _orchestrator.RecomputeArchiveAsync(A<OctoObjectId>._, A<DateTime>._, A<DateTime>._,
                A<OctoObjectId?>._, A<RecomputeTrigger>._, A<CancellationToken>._))
            .Throws(new ArchiveNotFoundException(RollupId));

        var outcome = await Recompute().RunAsync(_orchestrator, RollupId, From, From.AddDays(1), null, _progress,
            CancellationToken.None);

        outcome.Failure!.Code.Should().Be(ArchiveMethodErrors.FailureOf(new ArchiveNotFoundException(RollupId)).Code);
    }

    [Fact]
    public async Task BackfillFromSource2_AcceptsAtOnce_WithoutQueueingTheBackfill()
    {
        var outcome = await Backfill().HandleAsync(Call(SystemStreamDataCkIds.RollupArchiveBackfillFromSource2MethodId,
            new RollupArchiveBackfillFromSource2Parameters()), CancellationToken.None);

        outcome.IsAccepted.Should().BeTrue();
        A.CallTo(_orchestrator).MustNotHaveHappened();
    }

    [Fact]
    public async Task BackfillFromSource2Work_ReportsTheQueuedJob_FollowsIt_AndAnswersTheCompletedJob()
    {
        var jobId = OctoObjectId.GenerateNewId();
        A.CallTo(() => _orchestrator.EnqueueBackfillFromSourceAsync(RollupId, A<CancellationToken>._))
            .Returns(Job(jobId, RecomputeJobState.Pending));
        A.CallTo(() => _jobStore.GetAsync(jobId)).ReturnsNextFromSequence(
            Job(jobId, RecomputeJobState.Pending), Job(jobId, RecomputeJobState.Running), Job(jobId, RecomputeJobState.Completed));

        var outcome = await Backfill().RunAsync(_orchestrator, _jobStore, RollupId, TimeSpan.FromMilliseconds(1), _progress,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value!.Value!.JobRtId.Should().Be(jobId.ToString());
        outcome.Value.Value.State.Should().Be(RtCkRecomputeJobStateEnum.Completed);
        _progress.Reports.Select(r => r.StageCode).Should().Equal(RecomputeOperations.QueuedStage, "RUNNING");
        _progress.Reports.Should().OnlyContain(r => r.Parameters[ArchiveMethodErrors.JobRtIdParameter] == jobId.ToString());
    }

    [Fact]
    public async Task BackfillFromSource2Work_AFailedJob_IsRecomputeFailed_AndAnEmptySourceHasNoResult()
    {
        var jobId = OctoObjectId.GenerateNewId();
        A.CallTo(() => _orchestrator.EnqueueBackfillFromSourceAsync(RollupId, A<CancellationToken>._))
            .Returns(Job(jobId, RecomputeJobState.Pending));
        A.CallTo(() => _jobStore.GetAsync(jobId)).Returns(Job(jobId, RecomputeJobState.Failed, "boom"));

        var failed = await Backfill().RunAsync(_orchestrator, _jobStore, RollupId, TimeSpan.FromMilliseconds(1), _progress,
            CancellationToken.None);

        failed.Failure!.Code.Should().Be(ArchiveMethodErrors.RecomputeFailed);
        failed.Failure.Parameters[ArchiveMethodErrors.JobRtIdParameter].Should().Be(jobId.ToString());

        A.CallTo(() => _orchestrator.EnqueueBackfillFromSourceAsync(RollupId, A<CancellationToken>._))
            .Returns((RecomputeJobSnapshot?)null);
        var empty = await Backfill().RunAsync(_orchestrator, _jobStore, RollupId, TimeSpan.FromMilliseconds(1), _progress,
            CancellationToken.None);

        empty.IsSuccess.Should().BeTrue();
        empty.Value!.Value.Should().BeNull("an empty source archive has nothing to backfill");
    }

    [Fact]
    public async Task BackfillFromSource2Work_AJobThatDisappears_EndsTheOperation_InsteadOfWaitingForever()
    {
        var jobId = OctoObjectId.GenerateNewId();
        A.CallTo(() => _orchestrator.EnqueueBackfillFromSourceAsync(RollupId, A<CancellationToken>._))
            .Returns(Job(jobId, RecomputeJobState.Pending));
        A.CallTo(() => _jobStore.GetAsync(jobId)).Returns((RecomputeJobSnapshot?)null);

        var outcome = await Backfill().RunAsync(_orchestrator, _jobStore, RollupId, TimeSpan.FromMilliseconds(1), _progress,
            CancellationToken.None);

        outcome.Failure!.Code.Should().Be(ArchiveMethodErrors.RecomputeFailed);
        outcome.Failure.Parameters[ArchiveMethodErrors.ReasonParameter].Should().Be("job-not-found");
    }

    [Fact]
    public async Task BackfillFromSource2Work_ATransientReadError_DoesNotEndTheOperation()
    {
        var jobId = OctoObjectId.GenerateNewId();
        A.CallTo(() => _orchestrator.EnqueueBackfillFromSourceAsync(RollupId, A<CancellationToken>._))
            .Returns(Job(jobId, RecomputeJobState.Pending));
        A.CallTo(() => _jobStore.GetAsync(jobId))
            .Throws(new TimeoutException("primary election")).Once()
            .Then.Returns(Job(jobId, RecomputeJobState.Completed));

        var outcome = await Backfill().RunAsync(_orchestrator, _jobStore, RollupId, TimeSpan.FromMilliseconds(1), _progress,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue("one failed read must not decide the outcome of a job that keeps running");
        outcome.Value!.Value!.State.Should().Be(RtCkRecomputeJobStateEnum.Completed);
    }

    [Fact]
    public async Task BackfillFromSource2Work_PersistentReadErrors_EndTheOperation_AfterTheBound()
    {
        var jobId = OctoObjectId.GenerateNewId();
        A.CallTo(() => _orchestrator.EnqueueBackfillFromSourceAsync(RollupId, A<CancellationToken>._))
            .Returns(Job(jobId, RecomputeJobState.Pending));
        A.CallTo(() => _jobStore.GetAsync(jobId)).Throws(new TimeoutException("database down"));

        var run = () => Backfill().RunAsync(_orchestrator, _jobStore, RollupId, TimeSpan.FromMilliseconds(1), _progress,
            CancellationToken.None);

        await run.Should().ThrowAsync<TimeoutException>();
        A.CallTo(() => _jobStore.GetAsync(jobId)).MustHaveHappened(RecomputeOperations.MaxConsecutiveReadFailures,
            Times.Exactly);
    }

    [Fact]
    public async Task TheRestWait_RetriesATransientReadError()
    {
        var store = A.Fake<ICkMethodOperationStore>();
        var done = new CkMethodOperationRecord
        {
            TenantId = TenantId, OperationId = "op", MethodId = "m", InvocationId = Guid.NewGuid(), HandlerInstanceId = "h",
            State = CkMethodOperationState.Succeeded, AcceptedAtUtc = DateTime.UtcNow, HeartbeatAtUtc = DateTime.UtcNow
        };
        A.CallTo(() => store.GetAsync(TenantId, "op", A<CancellationToken>._))
            .Throws(new CkMethodOperationStoreException("election")).Once()
            .Then.Returns(done);

        var result = await RecomputeOperationWait.WaitAsync(store, TenantId, "op", RecomputeOperationWait.Ended,
            TimeSpan.FromMilliseconds(1), null, CancellationToken.None);

        result.Record.Should().BeSameAs(done);
    }

    [Fact]
    public void TheReasonOfAFailedJob_IsBounded()
    {
        var outcome = RecomputeOperations.OutcomeOf(Job(OctoObjectId.GenerateNewId(), RecomputeJobState.Failed,
            new string('x', 5000)));

        outcome.Failure!.Parameters[ArchiveMethodErrors.ReasonParameter].Length.Should().Be(RecomputeOperations.MaxReasonLength);
    }

    [Fact]
    public void TheRestWait_ReadsTheJobId_FromTheError_TheProgress_OrTheResult()
    {
        var jobId = OctoObjectId.GenerateNewId();
        var accepted = new CkMethodOperationRecord
        {
            TenantId = TenantId, OperationId = "op", MethodId = SystemStreamDataCkIds.RollupArchiveBackfillFromSource2MethodId,
            InvocationId = Guid.NewGuid(), HandlerInstanceId = "h", State = CkMethodOperationState.Running,
            AcceptedAtUtc = DateTime.UtcNow, HeartbeatAtUtc = DateTime.UtcNow
        };

        RecomputeOperationWait.JobRtIdOf(accepted).Should().BeNull();
        RecomputeOperationWait.EndedOrJobQueued(accepted).Should().BeFalse();

        var queued = accepted with
        {
            Progress = CkMethodOperationProgress.Create(null, RecomputeOperations.QueuedStage,
                (ArchiveMethodErrors.JobRtIdParameter, jobId.ToString()))
        };
        RecomputeOperationWait.JobRtIdOf(queued).Should().Be(jobId);
        RecomputeOperationWait.EndedOrJobQueued(queued).Should().BeTrue();
        RecomputeOperationWait.Ended(queued).Should().BeFalse();

        var succeeded = accepted with
        {
            State = CkMethodOperationState.Succeeded, ResultJson = "{\"value\":{\"jobRtId\":\"" + jobId + "\",\"state\":\"Completed\"}}"
        };
        RecomputeOperationWait.JobRtIdOf(succeeded).Should().Be(jobId);

        var empty = accepted with { State = CkMethodOperationState.Succeeded, ResultJson = """{"value":null}""" };
        RecomputeOperationWait.JobRtIdOf(empty).Should().BeNull();

        var failed = accepted with
        {
            State = CkMethodOperationState.Failed,
            Error = CkMethodError.Create(ArchiveMethodErrors.RecomputeFailed, (ArchiveMethodErrors.JobRtIdParameter, jobId.ToString()))
        };
        RecomputeOperationWait.JobRtIdOf(failed).Should().Be(jobId);
    }

    [Fact]
    public async Task TheRestWait_GivesUpAfterTheTimeout_AndOnAnUnknownRecord()
    {
        var store = A.Fake<ICkMethodOperationStore>();
        var running = new CkMethodOperationRecord
        {
            TenantId = TenantId, OperationId = "op", MethodId = "m", InvocationId = Guid.NewGuid(), HandlerInstanceId = "h",
            State = CkMethodOperationState.Running, AcceptedAtUtc = DateTime.UtcNow, HeartbeatAtUtc = DateTime.UtcNow
        };
        A.CallTo(() => store.GetAsync(TenantId, "op", A<CancellationToken>._)).Returns(running);
        A.CallTo(() => store.GetAsync(TenantId, "other", A<CancellationToken>._)).Returns((CkMethodOperationRecord?)null);

        var timedOut = await RecomputeOperationWait.WaitAsync(store, TenantId, "op", RecomputeOperationWait.Ended,
            TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(30), CancellationToken.None);
        timedOut.TimedOut.Should().BeTrue();
        timedOut.Record.Should().BeNull();

        var unknown = await RecomputeOperationWait.WaitAsync(store, TenantId, "other", RecomputeOperationWait.Ended,
            TimeSpan.FromMilliseconds(5), null, CancellationToken.None);
        unknown.TimedOut.Should().BeFalse();
        unknown.Record.Should().BeNull();
    }

    private sealed class RecordingProgress : ICkMethodOperationProgress
    {
        public List<CkMethodOperationProgress> Reports { get; } = [];

        public string OperationId => "op-1";

        public Task ReportAsync(CkMethodOperationProgress progress, CancellationToken cancellationToken = default)
        {
            progress.Validate();
            Reports.Add(progress);
            return Task.CompletedTask;
        }
    }
}
