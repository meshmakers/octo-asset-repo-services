using System.Reflection;
using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices;
using Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Controllers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AssetRepositoryServices.UnitTests.StreamData;

/// <summary>
///     AB#6968: the operator surface of the rollup validator and repair (<c>rollups/validate</c>,
///     <c>rollups/repair</c>) — mapping of the engine report, dry run by default, the apply path that is not
///     served inline, and the policies.
/// </summary>
public class StreamDataControllerRollupConsistencyTests
{
    private const string TenantId = "maco";

    private readonly OctoObjectId _rollupRtId = OctoObjectId.GenerateNewId();
    private readonly IStreamDataRepository _repository = A.Fake<IStreamDataRepository>();
    private readonly ITenantContext _tenantContext = A.Fake<ITenantContext>();
    private readonly StreamDataController _controller;

    public StreamDataControllerRollupConsistencyTests()
    {
        A.CallTo(() => _tenantContext.GetStreamDataRepository()).Returns(_repository);
        var systemContext = A.Fake<ISystemContext>();
        A.CallTo(() => systemContext.FindTenantContextAsync(TenantId)).Returns(_tenantContext);

        _controller = new StreamDataController(A.Fake<ILogger<StreamDataController>>(), systemContext,
            A.Fake<IHostApplicationLifetime>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private RollupConsistencyReport Broken() => new(
    [
        new RollupConsistencyEntry(_rollupRtId,
        [
            new RollupConsistencyFinding(RollupConsistencyCheck.OrphansAbovePointer, "rows above the pointer",
                new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc),
                3, 120, null),
            new RollupConsistencyFinding(RollupConsistencyCheck.WatermarkAheadOfRows, "watermark ahead", null, null,
                null, 7, TimeSpan.FromHours(2))
        ])
    ], DateTime.UtcNow);

    // ── validate ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Validate_WithoutIds_ChecksEveryRollupAndMapsTheReport()
    {
        A.CallTo(() => _repository.ValidateRollupsAsync(null, A<CancellationToken>._)).Returns(Broken());

        var result = await _controller.ValidateRollups(TenantId, null);

        var report = result.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<RollupConsistencyReportRestDto>().Subject;
        report.IsOk.Should().BeFalse();
        var entry = report.Entries.Should().ContainSingle().Subject;
        entry.RollupRtId.Should().Be(_rollupRtId.ToString());
        entry.IsOk.Should().BeFalse();
        entry.Findings.Select(f => f.Check).Should().Equal("OrphansAbovePointer", "WatermarkAheadOfRows");
        entry.Findings[0].Generation.Should().Be(3);
        entry.Findings[0].Rows.Should().Be(120);
        entry.Findings[1].Gap.Should().Be("02:00:00");
    }

    [Fact]
    public async Task Validate_WithIds_HandsThemToTheRepository()
    {
        A.CallTo(() => _repository.ValidateRollupsAsync(A<IReadOnlyCollection<OctoObjectId>?>._, A<CancellationToken>._))
            .Returns(new RollupConsistencyReport([], DateTime.UtcNow));

        var result = await _controller.ValidateRollups(TenantId, [_rollupRtId.ToString()]);

        result.Result.Should().BeOfType<OkObjectResult>();
        A.CallTo(() => _repository.ValidateRollupsAsync(
                A<IReadOnlyCollection<OctoObjectId>?>.That.Matches(ids => ids!.Single() == _rollupRtId),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Validate_InvalidRollupId_IsABadRequestAndNothingIsChecked()
    {
        var result = await _controller.ValidateRollups(TenantId, ["not-an-id"]);

        result.Result.Should().BeOfType<BadRequestObjectResult>().Which.Value!.ToString().Should().Contain("not-an-id");
        A.CallTo(() => _repository.ValidateRollupsAsync(A<IReadOnlyCollection<OctoObjectId>?>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task Validate_StreamDataNotEnabled_IsABadRequest()
    {
        A.CallTo(() => _tenantContext.GetStreamDataRepository()).Returns(null);

        var result = await _controller.ValidateRollups(TenantId, null);

        result.Result.Should().BeOfType<BadRequestObjectResult>().Which.Value!.ToString().Should().Contain("not enabled");
    }

    [Fact]
    public async Task Validate_RepositoryWithoutSupport_IsABadRequestWithTheReason()
    {
        A.CallTo(() => _repository.ValidateRollupsAsync(A<IReadOnlyCollection<OctoObjectId>?>._, A<CancellationToken>._))
            .Throws(new ArchiveCopyNotSupportedException(null));

        var result = await _controller.ValidateRollups(TenantId, null);

        result.Result.Should().BeOfType<BadRequestObjectResult>().Which.Value!.ToString()
            .Should().NotBeNullOrEmpty();
    }

    // ── repair ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Repair_DefaultsToADryRun_AndMapsThePlannedSteps()
    {
        var before = Broken();
        A.CallTo(() => _repository.RepairRollupsAsync(null, true, A<CancellationToken>._)).Returns(
            new RollupRepairReport(true, before,
            [
                new RollupRepairAction(_rollupRtId, RollupRepairKind.DeleteOrphans, "delete rows above the pointer",
                    RollupRepairStatus.Planned, null, null, 3, null)
            ], null, null));

        var result = await _controller.RepairRollups(TenantId, null);

        var report = result.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<RollupRepairReportRestDto>().Subject;
        report.DryRun.Should().BeTrue();
        report.After.Should().BeNull();
        report.JobIds.Should().BeEmpty();
        var action = report.Actions.Should().ContainSingle().Subject;
        action.Kind.Should().Be("DeleteOrphans");
        action.Status.Should().Be("Planned");
        action.Generation.Should().Be(3);
        A.CallTo(() => _repository.RepairRollupsAsync(A<IReadOnlyCollection<OctoObjectId>?>._, true,
            A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Repair_Apply_IsNotServedInlineAndChangesNothing()
    {
        var result = await _controller.RepairRollups(TenantId, null, dryRun: false);

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status501NotImplemented);
        A.CallTo(() => _repository.RepairRollupsAsync(A<IReadOnlyCollection<OctoObjectId>?>._, A<bool>._,
            A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Repair_InvalidRollupId_IsABadRequest()
    {
        var result = await _controller.RepairRollups(TenantId, ["zzz"]);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        A.CallTo(() => _repository.RepairRollupsAsync(A<IReadOnlyCollection<OctoObjectId>?>._, A<bool>._,
            A<CancellationToken>._)).MustNotHaveHappened();
    }

    // ── policies ───────────────────────────────────────────────────────────

    [Fact]
    public void Validate_IsReadOnly_AndRepairIsReadWrite()
    {
        PolicyOf(nameof(StreamDataController.ValidateRollups))
            .Should().Be(AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy);
        PolicyOf(nameof(StreamDataController.RepairRollups))
            .Should().Be(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy);
    }

    private static string? PolicyOf(string method) =>
        typeof(StreamDataController).GetMethod(method)!.GetCustomAttributes<AuthorizeAttribute>().Single().Policy;
}
