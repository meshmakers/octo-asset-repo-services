using System.Security.Claims;
using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;
using Meshmakers.Octo.Backend.AssetRepositoryServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.Backend.AssetRepositoryServices.UpgradeGuard;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.ConstructionKit.Contracts.Messages;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AssetRepositoryServices.UnitTests.TenantApi;

/// <summary>
/// AB#6871: the blueprint apply and update endpoints enforce the tenant upgrade guard like the import endpoints do
/// (AB#6870): a blocked CK update set is refused with 409 <c>CK_UPGRADE_BLOCKED</c> (problem details with the
/// structured blockers) before the first change, <c>ForceBreakingUpdate</c> needs the data model management policy and
/// leaves a WARN audit line with the blockers and the caller, the preview and a dry run only report the blockers.
/// Scenario: staging-1 tenant sbeg - Registration 2.8.0 is exact-pinned to Basic-2.4.0, the blueprint raises Basic to 2.5.0.
/// </summary>
public class BlueprintsControllerUpgradeGuardTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string TenantId = "sbeg";
    private const string Target = "Shop-2.0.0";

    private readonly IBlueprintService _blueprintService = A.Fake<IBlueprintService>();
    private readonly IAuthorizationService _authorization = A.Fake<IAuthorizationService>();
    private readonly ListLogger _logger = new();
    private readonly BlueprintsController _controller;

    public BlueprintsControllerUpgradeGuardTests()
    {
        _controller = new BlueprintsController(A.Fake<ITenantBlueprintHistory>(), _blueprintService,
            A.Fake<ITenantBlueprintInstallations>(), _authorization, _logger);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["tenantId"] = TenantId;
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "user-4711"), new Claim("name", "Ada Admin")], "Bearer"));
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        MayManageDataModel(true);
    }

    private void MayManageDataModel(bool allowed) =>
        A.CallTo(() => _authorization.AuthorizeAsync(A<ClaimsPrincipal>._, A<object?>._,
                AssetRepositoryServiceConstants.DataModelManagementPolicy))
            .Returns(allowed ? AuthorizationResult.Success() : AuthorizationResult.Failed());

    private static BlueprintCkUpgradeBlocker Registration() => new()
    {
        Dependent = "EnergyCommunity.Registration-2.8.0",
        Update = "Basic-2.5.0",
        Kind = "ExactPin",
        Subject = "Basic-2.4.0",
        Code = "CK_UPGRADE_EXACT_PIN",
        Message = "EnergyCommunity.Registration 2.8.0 would break: exact pin Basic-2.4.0: Basic-2.5.0 replaces it",
        SuggestedDependentUpdate = "EnergyCommunity.Registration-2.10.0",
        ResolveHint = "update EnergyCommunity.Registration to 2.10.0 first"
    };

    private static BlueprintUpdateResult BlockedUpdate() => new()
    {
        Success = false,
        IsCkUpgradeBlocked = true,
        CkUpdateSet = ["Basic-2.5.0"],
        CkUpgradeBlockers = [Registration()],
        Errors = ["engine text"]
    };

    private static BlueprintApplicationResult BlockedApply()
    {
        var op = new OperationResult();
        op.AddMessage(new OperationMessage(MessageLevel.Error, null, 55, "engine text"));
        return BlueprintApplicationResult.CkUpgradeBlocked("sbeg", new BlueprintId(Target), ["Basic-2.5.0"],
            [Registration()], op);
    }

    private void GivenUpdateReturns(BlueprintUpdateResult result) =>
        A.CallTo(() => _blueprintService.ApplyUpdateAsync(TenantId, A<BlueprintId>._, A<BlueprintUpdateMode>._,
                A<BlueprintUpdateOptions?>._, A<CancellationToken>._))
            .Returns(result);

    private void GivenApplyReturns(BlueprintApplicationResult result) =>
        A.CallTo(() => _blueprintService.ApplyBlueprintAsync(TenantId, A<BlueprintId>._, A<BlueprintApplyOptions>._,
                A<CancellationToken>._))
            .Returns(result);

    private static ProblemDetails AssertBlockedProblem(IActionResult result)
    {
        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        objectResult.ContentTypes.Should().Contain("application/problem+json");
        var problem = objectResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Extensions["code"].Should().Be("CK_UPGRADE_BLOCKED");
        problem.Extensions["updateSet"].Should().BeEquivalentTo(new[] { "Basic-2.5.0" });
        problem.Extensions["blueprintId"].Should().Be(Target);
        var blockers = problem.Extensions["blockers"].Should()
            .BeAssignableTo<IReadOnlyList<Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog.CkUpgradeBlockerDto>>()
            .Subject;
        var blocker = blockers.Should().ContainSingle().Subject;
        blocker.Dependent.Should().Be("EnergyCommunity.Registration-2.8.0");
        blocker.Subject.Should().Be("Basic-2.4.0");
        blocker.ResolveHint.Should().Be("update EnergyCommunity.Registration to 2.10.0 first");
        problem.Detail.Should().Contain("Nothing was changed").And.Contain("ForceBreakingUpdate");
        return problem;
    }

    // ---- ApplyUpdate ----

    [Fact]
    public async Task ApplyUpdate_BlockedSet_Answers409_AndAsksTheEngineToEnforce()
    {
        GivenUpdateReturns(BlockedUpdate());

        var result = await _controller.ApplyUpdate(new BlueprintUpdateRequestDto { TargetVersion = Target }, Ct);

        AssertBlockedProblem(result);
        A.CallTo(() => _blueprintService.ApplyUpdateAsync(TenantId, A<BlueprintId>._, A<BlueprintUpdateMode>._,
                A<BlueprintUpdateOptions?>.That.Matches(o => o!.CkUpgradeGuard == BlueprintCkUpgradeGuardMode.Enforce),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        _logger.Entries.Should().NotContain(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task ApplyUpdate_Forced_ProceedsWithTheForcedBlockers_AndLogsAWarningWithTheCaller()
    {
        MayManageDataModel(true);
        GivenUpdateReturns(new BlueprintUpdateResult
        {
            Success = true, CkUpdateSet = ["Basic-2.5.0"], CkUpgradeBlockers = [Registration()]
        });

        var result = await _controller.ApplyUpdate(
            new BlueprintUpdateRequestDto { TargetVersion = Target, ForceBreakingUpdate = true }, Ct);

        var dto = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should()
            .BeOfType<BlueprintUpdateResultDto>().Subject;
        dto.Success.Should().BeTrue();
        dto.Blockers.Should().ContainSingle().Which.Dependent.Should().Be("EnergyCommunity.Registration-2.8.0");
        dto.CkUpdateSet.Should().Equal("Basic-2.5.0");
        A.CallTo(() => _blueprintService.ApplyUpdateAsync(TenantId, A<BlueprintId>._, A<BlueprintUpdateMode>._,
                A<BlueprintUpdateOptions?>.That.Matches(o =>
                    o!.CkUpgradeGuard == BlueprintCkUpgradeGuardMode.ForceBreakingUpdate),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        var warning = _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning).Subject.Message;
        warning.Should().Contain("sbeg").And.Contain("sub=user-4711").And.Contain("name=Ada Admin")
            .And.Contain("EnergyCommunity.Registration-2.8.0").And.Contain("Basic-2.4.0").And.Contain(Target);
    }

    [Fact]
    public async Task ApplyUpdate_ForcedWithoutTheDataModelPolicy_IsForbidden_AndNothingIsCalled()
    {
        MayManageDataModel(false);

        var result = await _controller.ApplyUpdate(
            new BlueprintUpdateRequestDto { TargetVersion = Target, ForceBreakingUpdate = true }, Ct);

        result.Should().BeOfType<ForbidResult>();
        A.CallTo(_blueprintService).MustNotHaveHappened();
    }

    [Fact]
    public async Task ApplyUpdate_NotForced_DoesNotAskForThePolicy()
    {
        GivenUpdateReturns(new BlueprintUpdateResult { Success = true });

        var result = await _controller.ApplyUpdate(new BlueprintUpdateRequestDto { TargetVersion = Target }, Ct);

        result.Should().BeOfType<OkObjectResult>();
        A.CallTo(_authorization).MustNotHaveHappened();
    }

    [Fact]
    public async Task ApplyUpdate_DryRunWithBlockers_Answers200WithTheBlockers_AndLogsNothing()
    {
        GivenUpdateReturns(new BlueprintUpdateResult
        {
            Success = true, IsCkUpgradeBlocked = true, CkUpdateSet = ["Basic-2.5.0"], CkUpgradeBlockers = [Registration()]
        });

        var result = await _controller.ApplyUpdate(
            new BlueprintUpdateRequestDto { TargetVersion = Target, DryRun = true }, Ct);

        var dto = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should()
            .BeOfType<BlueprintUpdateResultDto>().Subject;
        dto.IsBlocked.Should().BeTrue();
        dto.Blockers.Should().ContainSingle();
        _logger.Entries.Should().NotContain(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task ApplyUpdate_TheGuardCouldNotJudge_Answers500_EvenWhenForced()
    {
        MayManageDataModel(true);
        GivenUpdateReturns(new BlueprintUpdateResult
        {
            Success = false, CkUpgradeGuardFailed = true, Errors = ["The CK upgrade guard could not judge: boom"]
        });

        var result = await _controller.ApplyUpdate(
            new BlueprintUpdateRequestDto { TargetVersion = Target, ForceBreakingUpdate = true }, Ct);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
    }

    [Fact]
    public async Task ApplyUpdate_AnotherFailure_StaysA400()
    {
        GivenUpdateReturns(new BlueprintUpdateResult { Success = false, Errors = ["conflict"] });

        var result = await _controller.ApplyUpdate(new BlueprintUpdateRequestDto { TargetVersion = Target }, Ct);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // ---- Apply (first time) ----

    [Fact]
    public async Task Apply_BlockedSet_Answers409_AndAsksTheEngineToEnforce()
    {
        GivenApplyReturns(BlockedApply());

        var result = await _controller.Apply(new BlueprintApplyRequestDto { BlueprintId = Target }, Ct);

        AssertBlockedProblem(result);
        A.CallTo(() => _blueprintService.ApplyBlueprintAsync(TenantId, A<BlueprintId>._,
                A<BlueprintApplyOptions>.That.Matches(o =>
                    o.CkUpgradeGuard == BlueprintCkUpgradeGuardMode.Enforce && !o.Force),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Apply_Forced_ProceedsAndLogsAWarningWithTheCaller()
    {
        var op = new OperationResult();
        GivenApplyReturns(BlueprintApplicationResult.Success(TenantId, new BlueprintId(Target), [], [], 0, op,
            [Registration()], ["Basic-2.5.0"]));

        var result = await _controller.Apply(
            new BlueprintApplyRequestDto { BlueprintId = Target, ForceBreakingUpdate = true, Force = true }, Ct);

        var dto = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should()
            .BeOfType<BlueprintApplyResultDto>().Subject;
        dto.Success.Should().BeTrue();
        dto.Blockers.Should().ContainSingle();
        A.CallTo(() => _blueprintService.ApplyBlueprintAsync(TenantId, A<BlueprintId>._,
                A<BlueprintApplyOptions>.That.Matches(o =>
                    o.CkUpgradeGuard == BlueprintCkUpgradeGuardMode.ForceBreakingUpdate && o.Force),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning && e.Message.Contains("sub=user-4711"));
    }

    [Fact]
    public async Task Apply_ForcedWithoutTheDataModelPolicy_IsForbidden()
    {
        MayManageDataModel(false);

        var result = await _controller.Apply(
            new BlueprintApplyRequestDto { BlueprintId = Target, ForceBreakingUpdate = true }, Ct);

        result.Should().BeOfType<ForbidResult>();
        A.CallTo(_blueprintService).MustNotHaveHappened();
    }

    [Fact]
    public async Task Apply_NothingBreaks_IsUnchanged()
    {
        GivenApplyReturns(BlueprintApplicationResult.Success(TenantId, new BlueprintId(Target), [], [], 3,
            new OperationResult()));

        var result = await _controller.Apply(new BlueprintApplyRequestDto { BlueprintId = Target }, Ct);

        var dto = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should()
            .BeOfType<BlueprintApplyResultDto>().Subject;
        dto.Blockers.Should().BeEmpty();
        dto.CkUpdateSet.Should().BeEmpty();
        _logger.Entries.Should().BeEmpty();
    }

    // ---- Preview ----

    [Fact]
    public async Task PreviewUpdate_ListsTheBlockers_WithoutRefusing()
    {
        A.CallTo(() => _blueprintService.PreviewUpdateAsync(TenantId, A<BlueprintId>._, A<BlueprintUpdateMode>._,
                A<CancellationToken>._))
            .Returns(new BlueprintUpdatePreview
            {
                CkUpdateSet = ["Basic-2.5.0"], CkUpgradeBlockers = [Registration()]
            });

        var result = await _controller.PreviewUpdate(new BlueprintUpdateRequestDto { TargetVersion = Target }, Ct);

        var dto = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should()
            .BeOfType<BlueprintUpdatePreviewDto>().Subject;
        dto.IsBlocked.Should().BeTrue();
        dto.CkUpdateSet.Should().Equal("Basic-2.5.0");
        var blocker = dto.Blockers.Should().ContainSingle().Subject;
        blocker.Dependent.Should().Be("EnergyCommunity.Registration-2.8.0");
        blocker.Subject.Should().Be("Basic-2.4.0");
    }

    [Fact]
    public async Task PreviewUpdate_NothingBreaks_HasNoBlockers()
    {
        A.CallTo(() => _blueprintService.PreviewUpdateAsync(TenantId, A<BlueprintId>._, A<BlueprintUpdateMode>._,
                A<CancellationToken>._))
            .Returns(new BlueprintUpdatePreview());

        var result = await _controller.PreviewUpdate(new BlueprintUpdateRequestDto { TargetVersion = Target }, Ct);

        var dto = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should()
            .BeOfType<BlueprintUpdatePreviewDto>().Subject;
        dto.IsBlocked.Should().BeFalse();
        dto.Blockers.Should().BeEmpty();
    }

    private sealed class ListLogger : ILogger<BlueprintsController>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }
}
