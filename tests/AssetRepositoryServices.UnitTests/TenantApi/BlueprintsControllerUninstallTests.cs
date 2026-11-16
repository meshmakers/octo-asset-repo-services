using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;
using Meshmakers.Octo.Backend.AssetRepositoryServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AssetRepositoryServices.UnitTests.TenantApi;

/// <summary>
/// AB#8966: <c>DELETE blueprints/{name}</c> reports the erased entities and the kept tenant-owned seed entities with the
/// reason, and <c>POST blueprints/{name}/uninstall/preview</c> runs the engine's dry run, never the uninstall.
/// </summary>
public class BlueprintsControllerUninstallTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string TenantId = "meshtest";
    private const string BlueprintName = "System.Ai.Builder";

    private readonly IBlueprintService _blueprintService = A.Fake<IBlueprintService>();
    private readonly BlueprintsController _controller;

    public BlueprintsControllerUninstallTests()
    {
        _controller = new BlueprintsController(
            A.Fake<ITenantBlueprintHistory>(),
            _blueprintService,
            A.Fake<ITenantBlueprintInstallations>(),
            A.Fake<IAuthorizationService>(),
            NullLogger<BlueprintsController>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["tenantId"] = TenantId;
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    private static BlueprintUninstallResult EngineResult(bool dryRun) => new()
    {
        Success = true,
        DryRun = dryRun,
        UninstalledBlueprintId = new BlueprintId($"{BlueprintName}-1.0.0"),
        EntitiesDeleted = 2,
        DeletedEntities =
        [
            new BlueprintUninstallDeletedEntity
            {
                Key = "page-a", CkTypeId = "System.Ai/KnowledgePage", EntityId = "aa0000000000000000000001",
                WellKnownName = "page-a", TenantOwned = true
            },
            new BlueprintUninstallDeletedEntity
            {
                Key = "space", CkTypeId = "System.Ai/KnowledgeSpace", EntityId = "aa0000000000000000000002",
                TenantOwned = false
            }
        ],
        TenantOwnedKept =
        [
            new BlueprintTenantOwnedKeptEntity
            {
                Key = "page-b", CkTypeId = "System.Ai/KnowledgePage", EntityId = "aa0000000000000000000003",
                Reason = BlueprintTenantOwnedKeptReason.Edited
            },
            new BlueprintTenantOwnedKeptEntity
            {
                Key = "page-b@1", CkTypeId = "System.Ai/KnowledgePageVersion", EntityId = "aa0000000000000000000004",
                Reason = BlueprintTenantOwnedKeptReason.Referenced, ReferencedBy = ["aa0000000000000000000003"]
            }
        ]
    };

    [Fact]
    public async Task Uninstall_MapsDeletedAndKeptEntities()
    {
        A.CallTo(() => _blueprintService.UninstallAsync(TenantId, BlueprintName, false, A<CancellationToken>._))
            .Returns(EngineResult(dryRun: false));

        var result = await _controller.Uninstall(BlueprintName, cancellationToken: Ct);

        var dto = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<BlueprintUninstallResultDto>().Subject;
        dto.DryRun.Should().BeFalse();
        dto.EntitiesDeleted.Should().Be(2);
        dto.DeletedEntities.Select(e => (e.Key, e.TenantOwned)).Should()
            .Equal(("page-a", true), ("space", false));
        dto.TenantOwnedKept.Select(e => (e.Key, e.Reason)).Should()
            .Equal(("page-b", "Edited"), ("page-b@1", "Referenced"));
        dto.TenantOwnedKept[1].ReferencedBy.Should().Equal("aa0000000000000000000003");
        dto.TenantOwnedKept[0].EntityId.Should().Be("aa0000000000000000000003");
        A.CallTo(() => _blueprintService.PreviewUninstallAsync(A<string>._, A<string>._, A<bool>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task PreviewUninstall_RunsThePreviewAndNeverTheUninstall()
    {
        A.CallTo(() => _blueprintService.PreviewUninstallAsync(TenantId, BlueprintName, true, A<CancellationToken>._))
            .Returns(EngineResult(dryRun: true));

        var result = await _controller.PreviewUninstall(BlueprintName, cascade: true, cancellationToken: Ct);

        var dto = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<BlueprintUninstallResultDto>().Subject;
        dto.DryRun.Should().BeTrue();
        dto.DeletedEntities.Should().HaveCount(2);
        dto.TenantOwnedKept.Should().HaveCount(2);
        A.CallTo(() => _blueprintService.UninstallAsync(A<string>._, A<string>._, A<bool>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task PreviewUninstall_BlockedByDependents_IsAConflictLikeTheUninstall()
    {
        A.CallTo(() => _blueprintService.PreviewUninstallAsync(TenantId, BlueprintName, false, A<CancellationToken>._))
            .Returns(new BlueprintUninstallResult
            {
                Success = false,
                DryRun = true,
                UninstalledBlueprintId = new BlueprintId($"{BlueprintName}-1.0.0"),
                BlockingDependents = [new BlueprintId("Addon-1.0.0")],
                Errors = ["blocked"]
            });

        var result = await _controller.PreviewUninstall(BlueprintName, cancellationToken: Ct);

        var dto = result.Should().BeOfType<ConflictObjectResult>().Subject
            .Value.Should().BeOfType<BlueprintUninstallResultDto>().Subject;
        dto.DryRun.Should().BeTrue();
        dto.BlockingDependents.Should().Equal("Addon-1.0.0");
    }

    [Fact]
    public async Task PreviewUninstall_NotInstalled_IsNotFound()
    {
        A.CallTo(() => _blueprintService.PreviewUninstallAsync(TenantId, BlueprintName, false, A<CancellationToken>._))
            .Returns(new BlueprintUninstallResult { Success = false, DryRun = true, Errors = ["not installed"] });

        var result = await _controller.PreviewUninstall(BlueprintName, cancellationToken: Ct);

        result.Should().BeOfType<NotFoundObjectResult>().Subject
            .Value.Should().BeOfType<OperationFailedErrorDto>().Subject.Message.Should().Be("not installed");
    }
}
