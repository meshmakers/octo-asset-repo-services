using System.Security.Claims;
using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.AssetRepositoryServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Tenant;

/// <summary>
/// AB#6871 (CK v2 F2.5): a blueprint update whose CK models would break an installed model is refused by the real
/// <see cref="BlueprintsController"/>, blueprint service and database with <c>409 CK_UPGRADE_BLOCKED</c> before the first
/// change - the installation record, the history and the CK models of the tenant stay as they were - while the preview
/// and a dry run only report the blockers and a forced update proceeds. Blueprint <c>CkGuardFlowBp</c> 1.0.0 needs System
/// only; 2.0.0 additionally installs the <c>AssetRepositoryIntegrationTest</c> CK model (so the update set is not empty);
/// what the guard says about that set is switched per test.
/// </summary>
[Collection(BlueprintUpdateFlowCollection.Name)]
public class BlueprintCkUpgradeGuardFlowTests(BlueprintUpdateFlowFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const int TestTimeoutMs = 180_000;
    private const string V1 = "CkGuardFlowBp-1.0.0";
    private const string V2 = "CkGuardFlowBp-2.0.0";
    private const string RaisedModel = "AssetRepositoryIntegrationTest";

    private static BlueprintCkUpgradeBlocker Blocker() => new()
    {
        Dependent = "EnergyCommunity.Registration-2.8.0",
        Update = "AssetRepositoryIntegrationTest-1.0.0",
        Kind = "ExactPin",
        Subject = "AssetRepositoryIntegrationTest-0.9.0",
        Code = "CK_UPGRADE_EXACT_PIN",
        Message = "EnergyCommunity.Registration 2.8.0 would break: exact pin"
    };

    [Fact(Timeout = TestTimeoutMs)]
    public async Task BlockedUpdate_IsRefusedWith409_AndTheTenantIsUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantId = await fixture.CreateTenantAsync("bt-ckg");
        try
        {
            await InstallV1Async(tenantId);
            var before = await SnapshotAsync(tenantId);
            before.CkModels.Should().NotContainKey(RaisedModel);
            fixture.CkUpgradeGuard.Block(Blocker());
            var controller = CreateController(tenantId, mayForce: true);

            var result = await controller.ApplyUpdate(new BlueprintUpdateRequestDto { TargetVersion = V2 }, ct);

            var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
            objectResult.StatusCode.Should().Be(StatusCodes.Status409Conflict);
            var problem = objectResult.Value.Should().BeOfType<ProblemDetails>().Subject;
            problem.Extensions["code"].Should().Be("CK_UPGRADE_BLOCKED");
            problem.Extensions["updateSet"].Should().BeEquivalentTo(new[] { "AssetRepositoryIntegrationTest-1.0.0" });
            fixture.CkUpgradeGuard.Asked.Should().ContainSingle().Which.Should()
                .Equal("AssetRepositoryIntegrationTest-1.0.0");

            (await SnapshotAsync(tenantId)).Should().BeEquivalentTo(before,
                "a refused update leaves the installation record, the history and the CK models untouched");
        }
        finally
        {
            await fixture.DropTenantAsync(tenantId);
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task PreviewAndDryRun_ListTheBlockers_AndWriteNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantId = await fixture.CreateTenantAsync("bt-ckp");
        try
        {
            await InstallV1Async(tenantId);
            var before = await SnapshotAsync(tenantId);
            fixture.CkUpgradeGuard.Block(Blocker());
            var controller = CreateController(tenantId, mayForce: true);

            var preview = AssertOk<BlueprintUpdatePreviewDto>(await controller.PreviewUpdate(
                new BlueprintUpdateRequestDto { TargetVersion = V2 }, ct));
            var dryRun = AssertOk<BlueprintUpdateResultDto>(await controller.ApplyUpdate(
                new BlueprintUpdateRequestDto { TargetVersion = V2, DryRun = true }, ct));

            preview.IsBlocked.Should().BeTrue();
            preview.Blockers.Should().ContainSingle().Which.Dependent.Should().Be("EnergyCommunity.Registration-2.8.0");
            preview.CkUpdateSet.Should().Equal("AssetRepositoryIntegrationTest-1.0.0");
            dryRun.Success.Should().BeTrue();
            dryRun.IsBlocked.Should().BeTrue();
            dryRun.Blockers.Should().ContainSingle();
            (await SnapshotAsync(tenantId)).Should().BeEquivalentTo(before);
        }
        finally
        {
            await fixture.DropTenantAsync(tenantId);
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ForcedUpdate_Proceeds_AndInstallsTheModel()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantId = await fixture.CreateTenantAsync("bt-ckf");
        try
        {
            await InstallV1Async(tenantId);
            fixture.CkUpgradeGuard.Block(Blocker());

            var result = AssertOk<BlueprintUpdateResultDto>(await CreateController(tenantId, mayForce: true).ApplyUpdate(
                new BlueprintUpdateRequestDto { TargetVersion = V2, ForceBreakingUpdate = true }, ct));

            result.Success.Should().BeTrue();
            result.Blockers.Should().ContainSingle("the forced blockers are reported");
            var after = await SnapshotAsync(tenantId);
            after.CkModels.Should().ContainKey(RaisedModel);
            after.Installed.Should().Contain(V2);
        }
        finally
        {
            await fixture.DropTenantAsync(tenantId);
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ForcedUpdate_WithoutThePolicy_IsForbidden_AndTheTenantIsUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantId = await fixture.CreateTenantAsync("bt-ckd");
        try
        {
            await InstallV1Async(tenantId);
            var before = await SnapshotAsync(tenantId);
            fixture.CkUpgradeGuard.Block(Blocker());

            var result = await CreateController(tenantId, mayForce: false).ApplyUpdate(
                new BlueprintUpdateRequestDto { TargetVersion = V2, ForceBreakingUpdate = true }, ct);

            result.Should().BeOfType<ForbidResult>();
            (await SnapshotAsync(tenantId)).Should().BeEquivalentTo(before);
        }
        finally
        {
            await fixture.DropTenantAsync(tenantId);
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task UpdateThatBreaksNothing_BehavesAsBefore()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantId = await fixture.CreateTenantAsync("bt-cko");
        try
        {
            await InstallV1Async(tenantId);
            fixture.CkUpgradeGuard.Block();

            var preview = AssertOk<BlueprintUpdatePreviewDto>(await CreateController(tenantId, mayForce: false)
                .PreviewUpdate(new BlueprintUpdateRequestDto { TargetVersion = V2 }, ct));
            var result = AssertOk<BlueprintUpdateResultDto>(await CreateController(tenantId, mayForce: false).ApplyUpdate(
                new BlueprintUpdateRequestDto { TargetVersion = V2 }, ct));

            preview.IsBlocked.Should().BeFalse();
            preview.Blockers.Should().BeEmpty();
            result.Success.Should().BeTrue();
            result.Blockers.Should().BeEmpty();
            (await SnapshotAsync(tenantId)).CkModels.Should().ContainKey(RaisedModel);
        }
        finally
        {
            await fixture.DropTenantAsync(tenantId);
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task FirstTimeApply_OfABlockedSet_IsRefusedWith409_AndTheTenantIsUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantId = await fixture.CreateTenantAsync("bt-ckn");
        try
        {
            var before = await SnapshotAsync(tenantId);
            fixture.CkUpgradeGuard.Block(Blocker());
            var controller = CreateController(tenantId, mayForce: true);

            var result = await controller.Apply(new BlueprintApplyRequestDto { BlueprintId = V2 }, ct);

            var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
            objectResult.StatusCode.Should().Be(StatusCodes.Status409Conflict);
            objectResult.Value.Should().BeOfType<ProblemDetails>().Which.Extensions["code"].Should().Be("CK_UPGRADE_BLOCKED");
            (await SnapshotAsync(tenantId)).Should().BeEquivalentTo(before);

            var forced = AssertOk<BlueprintApplyResultDto>(await controller.Apply(
                new BlueprintApplyRequestDto { BlueprintId = V2, ForceBreakingUpdate = true }, ct));
            forced.Success.Should().BeTrue();
            forced.Blockers.Should().ContainSingle();
            (await SnapshotAsync(tenantId)).CkModels.Should().ContainKey(RaisedModel);
        }
        finally
        {
            await fixture.DropTenantAsync(tenantId);
        }
    }

    private async Task InstallV1Async(string tenantId)
    {
        // The platform-internal apply (no guard), like a tenant setup.
        var applied = await fixture.GetBlueprintService().ApplyBlueprintAsync(tenantId, new BlueprintId(V1), false, Ct);
        applied.IsSuccess.Should().BeTrue(string.Join("; ", applied.OperationResult.Messages.Select(m => m.MessageText)));
    }

    /// <summary>What a refused operation must leave alone: CK models, installation rows and the history.</summary>
    private sealed record TenantSnapshot(Dictionary<string, string> CkModels, List<string> Installed, List<string> History);

    private async Task<TenantSnapshot> SnapshotAsync(string tenantId)
    {
        var models = await fixture.GetRuntimeRepositoryProvider().GetSchemaVersionsAsync(tenantId, Ct);
        var installed = (await fixture.GetBlueprintInstallations().GetInstalledAsync(tenantId, Ct))
            .Select(i => i.BlueprintId.FullName).Order().ToList();
        var history = (await fixture.GetBlueprintHistory().GetHistoryAsync(tenantId, Ct))
            .Select(h => $"{h.BlueprintId.FullName}|{h.ApplicationMode}").ToList();
        return new TenantSnapshot(models.ToDictionary(m => m.Key, m => m.Value), installed, history);
    }

    private BlueprintsController CreateController(string tenantId, bool mayForce)
    {
        var authorization = A.Fake<IAuthorizationService>();
        A.CallTo(() => authorization.AuthorizeAsync(A<ClaimsPrincipal>._, A<object?>._,
                AssetRepositoryServiceConstants.DataModelManagementPolicy))
            .Returns(mayForce ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        var controller = new BlueprintsController(
            fixture.GetBlueprintHistory(), fixture.GetBlueprintService(), fixture.GetBlueprintInstallations(),
            authorization, NullLogger<BlueprintsController>.Instance);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["tenantId"] = tenantId;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static T AssertOk<T>(IActionResult result) where T : class
    {
        var body = result.Should().BeOfType<OkObjectResult>().Subject.Value;
        return body.Should().BeOfType<T>().Subject;
    }
}
