using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;
using Meshmakers.Octo.Backend.AssetRepositoryServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AssetRepositoryServices.UnitTests.TenantApi;

/// <summary>
/// Covers the HTTP binding and fallback behaviour of the optional <c>blueprintName</c>
/// query parameter on the tenant blueprint reads (AB#4832). A tenant can host several
/// blueprints concurrently: without a name these endpoints describe the blueprint applied
/// last, with a name the one asked for.
/// </summary>
public class BlueprintsControllerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string TenantId = "meshtest";
    private const string BlueprintName = "EnergyCommunity.EdaIntegration";

    private readonly ITenantBlueprintHistory _blueprintHistory;
    private readonly IBlueprintService _blueprintService;
    private readonly BlueprintsController _controller;

    public BlueprintsControllerTests()
    {
        _blueprintHistory = A.Fake<ITenantBlueprintHistory>();
        _blueprintService = A.Fake<IBlueprintService>();

        _controller = new BlueprintsController(
            _blueprintHistory,
            _blueprintService,
            A.Fake<ITenantBlueprintInstallations>());

        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["tenantId"] = TenantId;
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    private static TenantBlueprintInfo HistoryEntry(string blueprintId)
    {
        return new TenantBlueprintInfo
        {
            BlueprintId = new BlueprintId(blueprintId),
            AppliedAt = new DateTime(2026, 8, 20, 10, 0, 0, DateTimeKind.Utc),
            ApplicationMode = BlueprintApplicationMode.Update
        };
    }

    #region GET current

    [Fact]
    public async Task GetCurrent_WithoutBlueprintName_UsesTheLastAppliedLookup()
    {
        A.CallTo(() => _blueprintHistory.GetCurrentAsync(TenantId, A<CancellationToken>._))
            .Returns(HistoryEntry("System.Identity.Bootstrap-1.2.0"));

        var result = await _controller.GetCurrent(cancellationToken: Ct);

        var dto = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<BlueprintHistoryItemDto>().Subject;
        dto.BlueprintId.Should().Be("System.Identity.Bootstrap-1.2.0");

        A.CallTo(() => _blueprintHistory.GetCurrentByBlueprintNameAsync(
                A<string>._, A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task GetCurrent_WithBlueprintName_UsesTheNameFilteredLookup()
    {
        A.CallTo(() => _blueprintHistory.GetCurrentByBlueprintNameAsync(
                TenantId, BlueprintName, A<CancellationToken>._))
            .Returns(HistoryEntry($"{BlueprintName}-2.2.0"));

        var result = await _controller.GetCurrent(BlueprintName, Ct);

        var dto = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<BlueprintHistoryItemDto>().Subject;
        dto.BlueprintId.Should().Be($"{BlueprintName}-2.2.0");

        A.CallTo(() => _blueprintHistory.GetCurrentAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task GetCurrent_WithBlankBlueprintName_FallsBackToTheLastAppliedLookup(
        string blueprintName)
    {
        // A blank argument keeps the parameter optional. The engine rejects a blank name as a
        // caller bug, so passing it through would turn into a 500 instead of the fallback.
        A.CallTo(() => _blueprintHistory.GetCurrentAsync(TenantId, A<CancellationToken>._))
            .Returns(HistoryEntry("System.Identity.Bootstrap-1.2.0"));

        var result = await _controller.GetCurrent(blueprintName, Ct);

        result.Should().BeOfType<OkObjectResult>();
        A.CallTo(() => _blueprintHistory.GetCurrentByBlueprintNameAsync(
                A<string>._, A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task GetCurrent_TrimsTheBlueprintName()
    {
        A.CallTo(() => _blueprintHistory.GetCurrentByBlueprintNameAsync(
                TenantId, BlueprintName, A<CancellationToken>._))
            .Returns(HistoryEntry($"{BlueprintName}-2.2.0"));

        await _controller.GetCurrent($"  {BlueprintName}  ", Ct);

        A.CallTo(() => _blueprintHistory.GetCurrentByBlueprintNameAsync(
                TenantId, BlueprintName, A<CancellationToken>._))
            .MustHaveHappened();
    }

    [Fact]
    public async Task GetCurrent_ReturnsNotFound_WhenTheBlueprintIsNotInstalled()
    {
        A.CallTo(() => _blueprintHistory.GetCurrentByBlueprintNameAsync(
                TenantId, BlueprintName, A<CancellationToken>._))
            .Returns((TenantBlueprintInfo?)null);

        var result = await _controller.GetCurrent(BlueprintName, Ct);

        result.Should().BeOfType<NotFoundResult>();
    }

    #endregion

    #region GET updates

    [Fact]
    public async Task GetAvailableUpdates_PassesTheBlueprintNameThrough()
    {
        A.CallTo(() => _blueprintService.GetUpdateInfoAsync(
                TenantId, BlueprintName, A<CancellationToken>._))
            .Returns(new BlueprintUpdateInfo
            {
                CurrentVersion = new BlueprintId($"{BlueprintName}-2.2.0"),
                AvailableVersions = [new BlueprintId($"{BlueprintName}-2.2.1")],
                RecommendedVersion = new BlueprintId($"{BlueprintName}-2.2.1")
            });

        var result = await _controller.GetAvailableUpdates(BlueprintName, Ct);

        var dto = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<BlueprintUpdateInfoDto>().Subject;
        dto.CurrentVersion.Should().Be("2.2.0");
        dto.RecommendedVersion.Should().Be($"{BlueprintName}-2.2.1");
        dto.HasUpdate.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task GetAvailableUpdates_WithoutBlueprintName_AsksForTheLastAppliedBlueprint(
        string? blueprintName)
    {
        // Return null explicitly: an unconfigured call would hand back a FakeItEasy dummy
        // whose required CurrentVersion is null, which no real engine result ever is.
        A.CallTo(() => _blueprintService.GetUpdateInfoAsync(
                TenantId, null, A<CancellationToken>._))
            .Returns((BlueprintUpdateInfo?)null);

        var result = await _controller.GetAvailableUpdates(blueprintName, Ct);

        result.Should().BeOfType<OkObjectResult>();
        A.CallTo(() => _blueprintService.GetUpdateInfoAsync(
                TenantId, null, A<CancellationToken>._))
            .MustHaveHappened();
    }

    #endregion

    [Fact]
    public async Task GetCurrent_ReturnsBadRequest_WhenRouteCarriesNoTenant()
    {
        _controller.ControllerContext.HttpContext.Request.RouteValues.Remove("tenantId");

        var result = await _controller.GetCurrent(BlueprintName, Ct);

        result.Should().BeOfType<BadRequestObjectResult>();
    }
}
