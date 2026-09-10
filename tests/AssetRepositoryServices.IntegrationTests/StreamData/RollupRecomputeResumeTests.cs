using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.StreamData;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
/// AB#5189 item A, against a REAL CrateDB: a multi-piece recompute (two source segments, chunked to
/// one bucket per chunk) that dies part-way records what it did not finish, a later drain completes
/// it without operator action, and the rollup then holds exactly the values a single uninterrupted
/// run produces. Two identical ladders over the same sources make that comparison literal: one is
/// recomputed in one go, the other is interrupted at the first chunk of its second segment.
/// </summary>
[Collection(StreamDataMutatingCollection.Name)]
public class RollupRecomputeResumeTests(StreamDataFixture fixture, ITestOutputHelper output)
{
    private readonly MultiSourceArchiveBuilder _builder = new(fixture);

    private const int Buckets = 6;
    private const int CutoverHour = 3;

    [Fact]
    public async Task AFailureInTheSecondSegment_LeavesTheRemainderQueued_AndTheDrainCompletesItToTheSameValues()
    {
        fixture.OutputHelper = output;
        var ladder = await BuildLadderPairAsync("Resume");

        // ── the reference: one uninterrupted run ─────────────────────────────────────────────────
        (await _builder.RecomputeAsync(ladder.Reference, ladder.Anchor, ladder.End)).State
            .Should().Be(RecomputeJobState.Completed);

        // ── the subject: the first chunk served by the second source blows up ────────────────────
        var executor = new FailingExecutor(ladder.RealExecutor,
            (source, _) => source.RtId == ladder.Native,
            () => new InvalidOperationException("injected: the second source cannot be read"));
        var job = await NewOrchestrator(ladder, executor).RecomputeArchiveAsync(
            ladder.Subject, ladder.Anchor, ladder.End, null, RecomputeTrigger.Manual, CancellationToken.None);

        job.State.Should().Be(RecomputeJobState.Failed);
        job.ErrorReason.Should().Contain("injected");
        job.RowsProcessed.Should().Be(CutoverHour, "the legacy segment's chunks committed before the failure");

        var pending = await PendingRangesAsync(ladder.Subject);
        var remainder = pending.Should().ContainSingle("exactly the unfinished tail is recorded").Subject;
        remainder.RangeStart.Should().Be(ladder.Cutover, "the committed prefix is not repeated");
        remainder.RangeEnd.Should().Be(ladder.End);
        remainder.Attempts.Should().Be(0, "a manual run's remainder starts its own retry budget");

        var partial = await _builder.ReadBucketsAsync(ladder.Subject, seriesRtId: ladder.Series);
        partial.Should().HaveCount(CutoverHour, "only the legacy segment's buckets exist so far");

        // ── a later drain — no operator involved — finishes the job ──────────────────────────────
        await NewOrchestrator(ladder, ladder.RealExecutor).TickAsync(CancellationToken.None);
        await _builder.RefreshAsync(ladder.Subject);
        await _builder.ExecuteSqlAsync($"REFRESH TABLE {_builder.GenMapTable(ladder.Subject)}");

        (await PendingRangesAsync(ladder.Subject)).Should().BeEmpty("the drain settled the obligation");
        await AssertSameValuesAsync(ladder);
    }

    [Fact]
    public async Task ACancellationMidRun_IsAnInterruptionNotAFailure_AndTheDrainCompletesItToTheSameValues()
    {
        fixture.OutputHelper = output;
        var ladder = await BuildLadderPairAsync("ResumeCancel");

        (await _builder.RecomputeAsync(ladder.Reference, ladder.Anchor, ladder.End)).State
            .Should().Be(RecomputeJobState.Completed);

        // Shutdown arrives exactly when the second segment starts: the executor observes the
        // cancelled token, as the CrateDB client would.
        using var cts = new CancellationTokenSource();
        var executor = new FailingExecutor(ladder.RealExecutor,
            (source, _) => source.RtId == ladder.Native,
            () =>
            {
                cts.Cancel();
                return new OperationCanceledException(cts.Token);
            });
        var job = await NewOrchestrator(ladder, executor).RecomputeArchiveAsync(
            ladder.Subject, ladder.Anchor, ladder.End, null, RecomputeTrigger.Manual, cts.Token);

        job.State.Should().Be(RecomputeJobState.Failed);
        job.ErrorReason.Should().StartWith("Interrupted");

        var remainder = (await PendingRangesAsync(ladder.Subject)).Should().ContainSingle().Subject;
        remainder.RangeStart.Should().Be(ladder.Cutover);
        remainder.Attempts.Should().Be(0);
        remainder.IsDueAt(DateTime.UtcNow).Should().BeTrue("an interruption imposes no backoff");

        await NewOrchestrator(ladder, ladder.RealExecutor).TickAsync(CancellationToken.None);
        await _builder.RefreshAsync(ladder.Subject);
        await _builder.ExecuteSqlAsync($"REFRESH TABLE {_builder.GenMapTable(ladder.Subject)}");

        (await PendingRangesAsync(ladder.Subject)).Should().BeEmpty();
        await AssertSameValuesAsync(ladder);
    }

    // ── ladder + helpers ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Two hourly rollups over the SAME two sources (legacy up to the cutover, native from it), with
    /// one point per bucket whose value identifies the bucket. Anchored well in the past so every
    /// bucket is lag-closed and the recompute covers all of them.
    /// </summary>
    private sealed record LadderPair(
        OctoObjectId Legacy,
        OctoObjectId Native,
        OctoObjectId Reference,
        OctoObjectId Subject,
        OctoObjectId Series,
        DateTime Anchor,
        DateTime Cutover,
        DateTime End,
        ITenantContext Tenant,
        IArchiveRecomputeExecutor RealExecutor);

    private async Task<LadderPair> BuildLadderPairAsync(string namePrefix)
    {
        var anchor = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, DateTime.UtcNow.Day, 0, 0, 0, DateTimeKind.Utc)
            .AddDays(-2);
        var cutover = anchor.AddHours(CutoverHour);
        var end = anchor.AddHours(Buckets);

        var legacy = await _builder.CreateRawArchiveAsync($"{namePrefix}Legacy");
        var native = await _builder.CreateRawArchiveAsync($"{namePrefix}Native");
        var series = OctoObjectId.GenerateNewId();
        await _builder.InsertPointsAsync(legacy, series,
            Enumerable.Range(0, CutoverHour).Select(i => (anchor.AddHours(i).AddMinutes(10), (i + 1) * 10.0)));
        await _builder.InsertPointsAsync(native, series,
            Enumerable.Range(CutoverHour, Buckets - CutoverHour).Select(i => (anchor.AddHours(i).AddMinutes(10), (i + 1) * 10.0)));

        var sources = new[]
        {
            new RollupSourceReference(legacy, ValidTo: cutover),
            new RollupSourceReference(native, ValidFrom: cutover),
        };
        var reference = await _builder.CreateRollupAsync($"{namePrefix}Reference", sources, TimeSpan.FromHours(1));
        var subject = await _builder.CreateRollupAsync($"{namePrefix}Subject", sources, TimeSpan.FromHours(1));
        await _builder.ActivateAsync(reference);
        await _builder.ActivateAsync(subject);

        var tenant = await _builder.TenantAsync();
        var realExecutor = (IArchiveRecomputeExecutor)tenant.GetStreamDataRepository()!;
        return new LadderPair(legacy, native, reference, subject, series, anchor, cutover, end, tenant, realExecutor);
    }

    /// <summary>
    /// An orchestrator over the tenant's real stores with an injectable executor and one bucket per
    /// chunk, so "multi-piece" means six chunks across two source segments.
    /// </summary>
    private static RecomputeOrchestrator NewOrchestrator(LadderPair ladder, IArchiveRecomputeExecutor executor)
    {
        var rollupStore = ladder.Tenant.GetRollupArchiveRuntimeStore()!;
        return new RecomputeOrchestrator(
            ladder.Tenant.TenantId,
            ladder.Tenant.GetArchiveRuntimeStore(),
            rollupStore,
            new RollupDependencyGraph(rollupStore),
            ladder.Tenant.GetArchiveRecomputeStateStore(),
            ladder.Tenant.GetRecomputeJobStore(),
            executor,
            ladder.Tenant.GetStreamDataRepository()!,
            new LoggingArchiveAuditTrail(NullLogger<LoggingArchiveAuditTrail>.Instance),
            NullLogger<RecomputeOrchestrator>.Instance,
            () => DateTime.UtcNow,
            maxBucketsPerChunk: 1);
    }

    private async Task AssertSameValuesAsync(LadderPair ladder)
    {
        var expected = await _builder.ReadBucketsAsync(ladder.Reference, seriesRtId: ladder.Series);
        var actual = await _builder.ReadBucketsAsync(ladder.Subject, seriesRtId: ladder.Series);

        expected.Should().HaveCount(Buckets, "the uninterrupted run materialised every bucket");
        actual.Should().Equal(expected,
            "after the interruption plus the following pass the rollup holds exactly what a single uninterrupted run produces");
    }

    private async Task<IReadOnlyList<ArchiveRecomputeRange>> PendingRangesAsync(OctoObjectId rollupRtId) =>
        await (await _builder.TenantAsync()).GetArchiveRecomputeStateStore().GetPendingRecomputeRangesAsync(rollupRtId);

    /// <summary>
    /// Delegates to the real executor except for the first call that matches the predicate, which
    /// throws the supplied exception instead — the injected failure of a multi-piece recompute.
    /// </summary>
    private sealed class FailingExecutor(
        IArchiveRecomputeExecutor inner,
        Func<ArchiveSnapshot, DateTime, bool> failWhen,
        Func<Exception> failure) : IArchiveRecomputeExecutor
    {
        private bool _armed = true;

        public Task<RecomputeExecutionResult> ExecuteAsync(
            ArchiveSnapshot source, RollupArchiveSnapshot rollup, DateTime rangeStart, DateTime rangeEnd,
            OctoObjectId? rtIdScope, CancellationToken cancellationToken)
        {
            if (_armed && failWhen(source, rangeStart))
            {
                _armed = false;
                throw failure();
            }

            return inner.ExecuteAsync(source, rollup, rangeStart, rangeEnd, rtIdScope, cancellationToken);
        }
    }
}
