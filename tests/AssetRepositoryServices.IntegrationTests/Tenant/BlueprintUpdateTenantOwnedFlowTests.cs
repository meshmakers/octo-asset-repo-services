using System.Text.Json;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.AssetRepositoryServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Tenant;

/// <summary>
/// AB#6454: preview and apply of a blueprint update report the tenant-owned seed entities
/// (<c>rtBlueprintLocked: false</c>, engine AB#6383) the update did not write, end to end through
/// <see cref="BlueprintsController"/>. Blueprint <c>TenantOwnedFlowBp</c> seeds two tenant-owned customers in
/// 1.0.0 and 2.0.0. On the tenant one is edited (kept, so skipped) and one is deleted (stays deleted).
/// </summary>
[Collection(BlueprintUpdateFlowCollection.Name)]
public class BlueprintUpdateTenantOwnedFlowTests(BlueprintUpdateFlowFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Per-test timeout: a stuck call fails the test instead of hanging the CI job.</summary>
    private const int TestTimeoutMs = 180_000;

    private const string V1 = "TenantOwnedFlowBp-1.0.0";
    private const string V2 = "TenantOwnedFlowBp-2.0.0";
    private const string KeptRtId = "67000099aaaa1111bbbb0011";
    private const string GoneRtId = "67000099aaaa1111bbbb0012";
    private const string TenantStreet = "Tenant Street Marker 4711";

    private static readonly RtCkId<CkTypeId> CustomerCkType = new("AssetRepositoryIntegrationTest/Customer");

    [Fact(Timeout = TestTimeoutMs)]
    public async Task PreviewAndApply_ListTheSkippedAndTheStaysDeletedTenantOwnedEntities()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantId = await fixture.CreateTenantAsync("bt-owned");
        try
        {
            await InstallV1AndEditAsync(tenantId);
            var controller = CreateController(tenantId);

            var preview = AssertOk<BlueprintUpdatePreviewDto>(await controller.PreviewUpdate(
                new BlueprintUpdateRequestDto { TargetVersion = V2, UpdateMode = "Merge" }, ct));

            AssertLists(preview.TenantOwnedSkipped, preview.TenantOwnedStaysDeleted);

            var result = AssertOk<BlueprintUpdateResultDto>(await controller.ApplyUpdate(
                new BlueprintUpdateRequestDto { TargetVersion = V2, UpdateMode = "Merge" }, ct));

            result.Success.Should().BeTrue();
            AssertLists(result.TenantOwnedSkipped, result.TenantOwnedStaysDeleted);
            result.EntitiesSkipped.Should().BeGreaterThanOrEqualTo(2, "both lists are counted in the skipped total");
            result.BlankedAttributes.Should().BeEmpty("tenant-owned entities are not blanking");

            // Untouched: the edited entity keeps its value, the deleted one is not back.
            (await ReadCustomersAsync(tenantId)).Should().BeEquivalentTo(new Dictionary<string, string?> { [KeptRtId] = TenantStreet });

            // Identity only: no response carries the tenant's value.
            foreach (var json in new[] { JsonSerializer.Serialize(preview), JsonSerializer.Serialize(result) })
            {
                json.Should().NotContain("Marker 4711");
            }
        }
        finally
        {
            await fixture.DropTenantAsync(tenantId);
        }
    }

    private static void AssertLists(
        List<BlueprintTenantOwnedEntityDto> skipped, List<BlueprintTenantOwnedEntityDto> staysDeleted)
    {
        var kept = skipped.Should().ContainSingle().Subject;
        kept.Key.Should().Be("OwnedKept");
        kept.EntityId.Should().Be(KeptRtId);
        kept.WellKnownName.Should().Be("OwnedKept");
        kept.CkTypeId.Should().Contain("Customer");

        var gone = staysDeleted.Should().ContainSingle().Subject;
        gone.Key.Should().Be("OwnedGone");
        gone.EntityId.Should().BeNull("the tenant deleted it");
        gone.CkTypeId.Should().Contain("Customer");
    }

    private async Task InstallV1AndEditAsync(string tenantId)
    {
        var applied = await fixture.GetBlueprintService().ApplyBlueprintAsync(tenantId, new BlueprintId(V1), false, Ct);
        applied.IsSuccess.Should().BeTrue(string.Join("; ", applied.OperationResult.Messages.Select(m => m.MessageText)));
        (await ReadCustomersAsync(tenantId)).Keys.Should().BeEquivalentTo([KeptRtId, GoneRtId], "1.0.0 seeds both");

        var repository = await fixture.GetRuntimeRepositoryProvider().GetRepositoryAsync(tenantId, Ct);
        repository.Should().NotBeNull();
        using var session = await repository!.GetSessionAsync();
        var customers = await repository.GetRtEntitiesByTypeAsync(session, CustomerCkType, RtEntityQueryOptions.Create());
        var kept = customers.Items.Single(c => c.RtId.ToString() == KeptRtId);
        kept.SetAttributeRawValue("Street", TenantStreet);
        session.StartTransaction();
        await repository.ReplaceOneRtEntityByIdAsync(session, CustomerCkType, kept.RtId, kept);
        await repository.DeleteOneRtEntityByRtIdAsync(session, CustomerCkType, new OctoObjectId(GoneRtId), DeleteOptions.Erase);
        await session.CommitTransactionAsync();
    }

    private async Task<Dictionary<string, string?>> ReadCustomersAsync(string tenantId)
    {
        var repository = await fixture.GetRuntimeRepositoryProvider().GetRepositoryAsync(tenantId, Ct);
        repository.Should().NotBeNull();
        using var session = await repository!.GetSessionAsync();
        var customers = await repository.GetRtEntitiesByTypeAsync(session, CustomerCkType, RtEntityQueryOptions.Create());
        return customers.Items.ToDictionary(c => c.RtId.ToString(), c => c.GetAttributeStringValueOrDefault("Street"));
    }

    private BlueprintsController CreateController(string tenantId)
    {
        var controller = new BlueprintsController(
            fixture.GetBlueprintHistory(), fixture.GetBlueprintService(), fixture.GetBlueprintInstallations());
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
