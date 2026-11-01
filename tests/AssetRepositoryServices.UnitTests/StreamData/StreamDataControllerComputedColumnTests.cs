using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Controllers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AssetRepositoryServices.UnitTests.StreamData;

/// <summary>
///     AB#8002: the computed-column REST endpoints queue the backfill instead of running it inside the request — they call
///     the engine's Enqueue methods with the host's stopping token (never the request's) and never the inline variants —
///     and the retry endpoint returns the queued job.
/// </summary>
public class StreamDataControllerComputedColumnTests
{
    private const string TenantId = "energyiq";
    private static readonly OctoObjectId Archive = new("6a0e000000000000000a0007");

    private readonly IArchiveLifecycleService _lifecycle = A.Fake<IArchiveLifecycleService>();
    private readonly CancellationTokenSource _stopping = new();
    private readonly StreamDataController _controller;

    public StreamDataControllerComputedColumnTests()
    {
        var tenantContext = A.Fake<ITenantContext>();
        A.CallTo(() => tenantContext.GetArchiveLifecycleService()).Returns(_lifecycle);
        var systemContext = A.Fake<ISystemContext>();
        A.CallTo(() => systemContext.FindTenantContextAsync(TenantId)).Returns(tenantContext);
        var lifetime = A.Fake<IHostApplicationLifetime>();
        A.CallTo(() => lifetime.ApplicationStopping).Returns(_stopping.Token);

        _controller = new StreamDataController(A.Fake<ILogger<StreamDataController>>(), systemContext, lifetime);
    }

    private static RecomputeJobSnapshot Queued(string column) =>
        RecomputeJobSnapshot.ForComputedColumn(Archive, column, RecomputeJobState.Pending, RecomputeTrigger.Manual,
            new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc)) with { RtId = OctoObjectId.GenerateNewId() };

    [Fact]
    public async Task Add_QueuesTheBackfill_WithTheHostToken_AndNeverBackfillsInline()
    {
        var result = await _controller.AddComputedColumn(TenantId, Archive.ToString(), "ExportPower",
            "activepower * -1", FormulaResultType.Double);

        result.Should().BeOfType<NoContentResult>();
        A.CallTo(() => _lifecycle.EnqueueAddComputedColumnAsync(Archive, "ExportPower", "activepower * -1",
            FormulaResultType.Double, true, _stopping.Token)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _lifecycle.AddComputedColumnAsync(A<OctoObjectId>._, A<string>._, A<string>._,
            A<FormulaResultType>._, A<bool>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task UpdateFormula_QueuesTheBackfill_AndNeverBackfillsInline()
    {
        var result = await _controller.UpdateComputedColumnFormula(TenantId, Archive.ToString(), "ExportPower",
            "activepower * -2");

        result.Should().BeOfType<NoContentResult>();
        A.CallTo(() => _lifecycle.EnqueueUpdateComputedColumnFormulaAsync(Archive, "ExportPower", "activepower * -2",
            _stopping.Token)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _lifecycle.UpdateComputedColumnFormulaAsync(A<OctoObjectId>._, A<string>._, A<string>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task UpdateFormula_WhileABackfillRuns_IsABadRequest()
    {
        A.CallTo(() => _lifecycle.EnqueueUpdateComputedColumnFormulaAsync(A<OctoObjectId>._, A<string>._, A<string>._,
                A<CancellationToken>._))
            .Throws(new ComputedColumnInvalidException(Archive, "ExportPower", "a backfill is still queued or running"));

        var result = await _controller.UpdateComputedColumnFormula(TenantId, Archive.ToString(), "ExportPower", "x");

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Retry_ReturnsTheQueuedJob_WithItsColumn()
    {
        A.CallTo(() => _lifecycle.RetryComputedColumnBackfillAsync(Archive, "ExportPower", _stopping.Token))
            .Returns(Queued("ExportPower"));

        var result = await _controller.RetryComputedColumnBackfill(TenantId, Archive.ToString(), "ExportPower");

        var job = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<RecomputeJobInfoRestDto>().Subject;
        job.State.Should().Be("Pending");
        job.ComputedColumnName.Should().Be("ExportPower");
    }

    [Fact]
    public async Task Retry_OfAnActiveColumn_IsABadRequest()
    {
        A.CallTo(() => _lifecycle.RetryComputedColumnBackfillAsync(A<OctoObjectId>._, A<string>._, A<CancellationToken>._))
            .Throws(new ComputedColumnInvalidException(Archive, "ExportPower", "computed column 'ExportPower' is Active"));

        var result = await _controller.RetryComputedColumnBackfill(TenantId, Archive.ToString(), "ExportPower");

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Retry_OfAnUnknownColumn_IsABadRequest()
    {
        A.CallTo(() => _lifecycle.RetryComputedColumnBackfillAsync(A<OctoObjectId>._, A<string>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("Computed column 'Nope' does not exist"));

        var result = await _controller.RetryComputedColumnBackfill(TenantId, Archive.ToString(), "Nope");

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }
}
