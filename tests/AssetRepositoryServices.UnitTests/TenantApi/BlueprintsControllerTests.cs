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

    #region POST updates/preview

    [Fact]
    public async Task PreviewUpdate_ReturnsTheAttributeLevelChangeList()
    {
        // AB#5297 / AB#5308: the counts alone told the operator nothing; the controller must
        // hand the engine's per-entity diff through, values rendered as text.
        A.CallTo(() => _blueprintService.PreviewUpdateAsync(
                TenantId, A<BlueprintId>.That.Matches(b => b.ToString() == $"{BlueprintName}-2.2.1"),
                BlueprintUpdateMode.Merge, A<CancellationToken>._))
            .Returns(new BlueprintUpdatePreview
            {
                EntitiesToUpdate = 1,
                EntitiesUnchanged = 41,
                Changes =
                [
                    new BlueprintEntityChange
                    {
                        EntityId = "67d4a2f0b2e4d8c3a1f00131",
                        EntityDisplayName = "Notify ToDo Pipeline",
                        EntityCkTypeId = "System.Communication/Pipeline",
                        Attributes =
                        [
                            new BlueprintAttributeChange { AttributeName = "Enabled", OldValue = true, NewValue = false }
                        ]
                    }
                ]
            });

        var result = await _controller.PreviewUpdate(
            new BlueprintUpdateRequestDto { TargetVersion = $"{BlueprintName}-2.2.1", UpdateMode = "merge" }, Ct);

        var dto = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<BlueprintUpdatePreviewDto>().Subject;
        dto.TargetVersion.Should().Be($"{BlueprintName}-2.2.1");
        dto.EntitiesToUpdate.Should().Be(1);
        dto.EntitiesUnchanged.Should().Be(41);
        var change = dto.Changes.Should().ContainSingle().Subject;
        change.EntityDisplayName.Should().Be("Notify ToDo Pipeline");
        var attribute = change.Attributes.Should().ContainSingle().Subject;
        attribute.AttributeName.Should().Be("Enabled");
        attribute.OldValue.Should().Be("true");
        attribute.NewValue.Should().Be("false");
    }

    #endregion

    #region AB#6315 blanking preview and confirmation

    private static BlueprintBlankedAttribute Blanked(bool applied) => new()
    {
        RtId = "67d4a2f0b2e4d8c3a1f00131",
        CkTypeId = "System.Communication/Adapter",
        AttributeName = "Configuration",
        Reason = "SeedEmpty",
        CurrentSummary = "string (223 chars)",
        IncomingSummary = "string (110 chars)",
        AppliedOnUpdate = applied
    };

    [Fact]
    public async Task PreviewUpdate_ListsTheAttributesTheSeedWouldBlank_WithoutValues()
    {
        A.CallTo(() => _blueprintService.PreviewUpdateAsync(
                TenantId, A<BlueprintId>._, A<BlueprintUpdateMode>._, A<CancellationToken>._))
            .Returns(new BlueprintUpdatePreview { BlankedAttributes = [Blanked(applied: false)] });

        var result = await _controller.PreviewUpdate(
            new BlueprintUpdateRequestDto { TargetVersion = $"{BlueprintName}-2.10.2" }, Ct);

        var dto = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<BlueprintUpdatePreviewDto>().Subject;
        var blanked = dto.BlankedAttributes.Should().ContainSingle().Subject;
        blanked.RtId.Should().Be("67d4a2f0b2e4d8c3a1f00131");
        blanked.AttributeName.Should().Be("Configuration");
        blanked.Reason.Should().Be("SeedEmpty");
        blanked.CurrentSummary.Should().Be("string (223 chars)");
        blanked.IncomingSummary.Should().Be("string (110 chars)");
        blanked.AppliedOnUpdate.Should().BeFalse();
    }

    [Fact]
    public async Task ApplyUpdate_WithoutConfirmation_KeepsByDefault_AndReturnsTheKeptList()
    {
        BlueprintUpdateOptions? seen = null;
        A.CallTo(() => _blueprintService.ApplyUpdateAsync(
                TenantId, A<BlueprintId>._, A<BlueprintUpdateMode>._, A<BlueprintUpdateOptions?>._, A<CancellationToken>._))
            .Invokes((string _, BlueprintId _, BlueprintUpdateMode _, BlueprintUpdateOptions? o, CancellationToken _) => seen = o)
            .Returns(new BlueprintUpdateResult
            {
                Success = true,
                EntitiesUpdated = 3,
                BlankedAttributes = [Blanked(applied: false)]
            });

        // An old client: no blanking fields at all.
        var result = await _controller.ApplyUpdate(
            new BlueprintUpdateRequestDto { TargetVersion = $"{BlueprintName}-2.10.2" }, Ct);

        seen.Should().NotBeNull();
        seen!.AllowBlanking.Should().BeFalse();
        seen.ConfirmedBlankings.Should().BeNull();
        var dto = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<BlueprintUpdateResultDto>().Subject;
        dto.Success.Should().BeTrue();
        dto.EntitiesUpdated.Should().Be(3);
        dto.BlankedAttributes.Should().ContainSingle().Which.AppliedOnUpdate.Should().BeFalse();
    }

    [Fact]
    public async Task ApplyUpdate_AllowBlanking_MapsToTheEngineOption()
    {
        BlueprintUpdateOptions? seen = null;
        A.CallTo(() => _blueprintService.ApplyUpdateAsync(
                TenantId, A<BlueprintId>._, A<BlueprintUpdateMode>._, A<BlueprintUpdateOptions?>._, A<CancellationToken>._))
            .Invokes((string _, BlueprintId _, BlueprintUpdateMode _, BlueprintUpdateOptions? o, CancellationToken _) => seen = o)
            .Returns(new BlueprintUpdateResult { Success = true, BlankedAttributes = [Blanked(applied: true)] });

        var result = await _controller.ApplyUpdate(
            new BlueprintUpdateRequestDto { TargetVersion = $"{BlueprintName}-2.10.2", AllowBlanking = true }, Ct);

        seen!.AllowBlanking.Should().BeTrue();
        result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<BlueprintUpdateResultDto>().Subject
            .BlankedAttributes.Should().ContainSingle().Which.AppliedOnUpdate.Should().BeTrue();
    }

    [Fact]
    public async Task ApplyUpdate_ConfirmedPairs_AreMappedExactly_AndEmptyOnesAreDroppedNotWidened()
    {
        BlueprintUpdateOptions? seen = null;
        A.CallTo(() => _blueprintService.ApplyUpdateAsync(
                TenantId, A<BlueprintId>._, A<BlueprintUpdateMode>._, A<BlueprintUpdateOptions?>._, A<CancellationToken>._))
            .Invokes((string _, BlueprintId _, BlueprintUpdateMode _, BlueprintUpdateOptions? o, CancellationToken _) => seen = o)
            .Returns(new BlueprintUpdateResult { Success = true });

        await _controller.ApplyUpdate(new BlueprintUpdateRequestDto
        {
            TargetVersion = $"{BlueprintName}-2.10.2",
            ConfirmedBlankings =
            [
                new BlueprintBlankingConfirmationDto { RtId = "67d4a2f0b2e4d8c3a1f00131", AttributeName = "Configuration" },
                new BlueprintBlankingConfirmationDto { RtId = "", AttributeName = "Configuration" },
                new BlueprintBlankingConfirmationDto { RtId = "67d4a2f0b2e4d8c3a1f00132", AttributeName = " " }
            ]
        }, Ct);

        seen!.AllowBlanking.Should().BeFalse();
        var pair = seen.ConfirmedBlankings.Should().ContainSingle().Subject;
        pair.RtId.Should().Be("67d4a2f0b2e4d8c3a1f00131");
        pair.AttributeName.Should().Be("Configuration");
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
