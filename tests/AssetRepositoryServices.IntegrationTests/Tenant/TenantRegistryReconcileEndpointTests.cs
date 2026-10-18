using System.Security.Claims;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.AssetRepositoryServices.SystemApi.v1.Controllers;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Tenant;

/// <summary>
///     AB#6971 / AB#6941: <c>POST system/v1/tenants/reconcile-registry</c> against a real MongoDB with the engine's
///     reconcile. A nested tenant that its parent lists but the system registry lacks (the ec_subtenant_test state)
///     is reported by the dry run without a write, inserted by apply, and a second apply inserts nothing.
/// </summary>
[Collection(AssetRepoCollection.Name)]
public class TenantRegistryReconcileEndpointTests(AssetRepoFixture fixture)
{
    private ISystemContext SystemContext => fixture.GetSystemContext();

    private TenantRegistryController CreateController(ClaimsPrincipal user) =>
        new(SystemContext, NullLogger<TenantRegistryController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } }
        };

    private ClaimsPrincipal SystemAdmin() => new(new ClaimsIdentity(
    [
        new Claim("sub", "system-admin"),
        new Claim("tenant_id", SystemContext.TenantId),
        new Claim(ClaimTypes.Role, CommonConstants.AdminPanelManagementRole)
    ], "IntegrationTests"));

    private async Task CreateAsync(ITenantContext parent, string tenantId)
    {
        using var session = await parent.GetAdminSessionAsync();
        session.StartTransaction();
        await parent.CreateChildTenantAsync(session, tenantId, tenantId);
        await session.CommitTransactionAsync();
    }

    private async Task DropAsync(ITenantContext parent, string tenantId)
    {
        try
        {
            using var session = await parent.GetAdminSessionAsync();
            session.StartTransaction();
            await parent.DropChildTenantAsync(session, tenantId);
            await session.CommitTransactionAsync();
        }
        catch (Exception)
        {
            // cleanup only
        }
    }

    private async Task<OctoTenant[]> RegistryAsync()
    {
        using var session = await SystemContext.GetAdminSessionAsync();
        return (await SystemContext.GetAllTenantsAsync(session)).Items.ToArray();
    }

    private static TenantRegistryReconcileReportDto Report(IActionResult result) =>
        result.Should().BeOfType<OkObjectResult>().Subject.Value.Should()
            .BeOfType<TenantRegistryReconcileReportDto>().Subject;

    [Fact]
    public async Task Reconcile_DryRunReports_ApplyInserts_SecondApplyInsertsNothing_AndOthersGetForbidden()
    {
        var parentId = $"rec-par-{Guid.NewGuid():N}"[..20];
        var lostId = $"rec-lost-{Guid.NewGuid():N}"[..20];
        await CreateAsync(SystemContext, parentId);
        ITenantContext parent;
        using (var session = await SystemContext.GetAdminSessionAsync())
        {
            session.StartTransaction();
            parent = await SystemContext.GetChildTenantContextAsync(session, parentId);
            await session.CommitTransactionAsync();
        }

        try
        {
            await CreateAsync(parent, lostId);
            using (var session = await SystemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await SystemContext.GetTenantRepositoryAsAdmin().DeleteOneRtEntityAsync<RtTenant>(session,
                    FieldFilterCriteria.Create().FieldEquals(nameof(RtTenant.TenantId), lostId),
                    Meshmakers.Octo.Runtime.Contracts.Repositories.DeleteOptions.Erase);
                await session.CommitTransactionAsync();
            }

            var controller = CreateController(SystemAdmin());

            var dryRun = Report(await controller.ReconcileRegistry());
            dryRun.Applied.Should().BeFalse();
            dryRun.Inserted.Should().ContainSingle(e => e.TenantId == lostId && e.ParentTenantId == parentId);
            (await RegistryAsync()).Should().NotContain(t => t.TenantId == lostId, "a dry run writes nothing");

            var applied = Report(await controller.ReconcileRegistry(apply: true));
            applied.Applied.Should().BeTrue();
            applied.Inserted.Should().ContainSingle(e => e.TenantId == lostId);
            (await RegistryAsync()).Should().ContainSingle(t => t.TenantId == lostId && t.ParentTenantId == parentId);

            var again = Report(await controller.ReconcileRegistry(apply: true));
            again.Inserted.Should().NotContain(e => e.TenantId == lostId);
            (await RegistryAsync()).Count(t => t.TenantId == lostId).Should().Be(1);

            var other = CreateController(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("sub", "x"), new Claim("tenant_id", "customer"),
                 new Claim(ClaimTypes.Role, CommonConstants.AdminPanelManagementRole)], "IntegrationTests")));
            (await other.ReconcileRegistry(apply: true)).Should().BeOfType<ForbidResult>();
        }
        finally
        {
            await DropAsync(parent, lostId);
            await DropAsync(SystemContext, parentId);
        }
    }
}
