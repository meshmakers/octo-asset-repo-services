using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.AssetRepositoryServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Tenant;

/// <summary>
///     AB#4884 — <c>GET {tenantId}/v1/features/status</c> reports the capability flags exactly as the
///     owning services persist them, through the same reader the delete/detach guard evaluates
///     (AB#4255). Exercises the real controller against a real MongoDB: flags written with the engine
///     calls the owning code uses (<c>SetConfigurationAsync</c> in the Standardized creator's value
///     shape, <c>DeleteConfigurationAsync</c> as its Disable does) must show up in the aggregate.
/// </summary>
[Collection(AssetRepoCollection.Name)]
public class FeaturesStatusEndpointTests(AssetRepoFixture fixture)
{
    private static FeaturesController CreateController(ISystemContext systemContext, bool instanceEnabled) => new(
        systemContext,
        new TenantCapabilityStateReader(NullLogger<TenantCapabilityStateReader>.Instance),
        Options.Create(new StreamDataInstanceConfiguration { Enabled = instanceEnabled }));

    private static TenantFeaturesStatusDto Unwrap(ActionResult<TenantFeaturesStatusDto> result) =>
        result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<TenantFeaturesStatusDto>().Subject;

    private static async Task<string> CreateTempChildAsync(ISystemContext systemContext)
    {
        var tenantId = $"temp-feat-{Guid.NewGuid():N}";
        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();
        await systemContext.CreateChildTenantAsync(session, tenantId, tenantId);
        await session.CommitTransactionAsync();
        return tenantId;
    }

    private static async Task DropAsync(ISystemContext systemContext, string tenantId)
    {
        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();
        await systemContext.DropChildTenantAsync(session, tenantId);
        await session.CommitTransactionAsync();
    }

    private static async Task<ITenantContext> GetChildAsync(ISystemContext systemContext, string tenantId)
    {
        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();
        var child = await systemContext.GetChildTenantContextAsync(session, tenantId);
        await session.CommitTransactionAsync();
        return child;
    }

    private static async Task SetAsync(ITenantContext child, string key, object value)
    {
        using var session = await child.GetAdminSessionAsync();
        session.StartTransaction();
        await child.SetConfigurationAsync(session, key, value);
        await session.CommitTransactionAsync();
    }

    private static async Task DeleteAsync(ITenantContext child, string key)
    {
        using var session = await child.GetAdminSessionAsync();
        session.StartTransaction();
        await child.DeleteConfigurationAsync(session, key);
        await session.CommitTransactionAsync();
    }

    [Fact]
    public async Task Status_ReflectsTheFlags_AsTheOwningServicesPersistThem()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = await CreateTempChildAsync(systemContext);
        try
        {
            var controller = CreateController(systemContext, instanceEnabled: true);

            var initial = Unwrap(await controller.Status(tenantId));
            initial.StreamData.InstanceEnabled.Should().BeTrue();
            initial.StreamData.TenantEnabled.Should().BeFalse();
            initial.Communication.TenantEnabled.Should().BeFalse();
            initial.Reporting.TenantEnabled.Should().BeFalse();
            initial.AiServices.TenantEnabled.Should().BeFalse();

            var child = await GetChildAsync(systemContext, tenantId);
            var enabled = new DefaultConfigurationEnabled { IsEnabled = true };
            await SetAsync(child, TenantCapabilityConfigurationKeys.Communication, enabled);
            await SetAsync(child, TenantCapabilityConfigurationKeys.AiServices, enabled);

            var afterEnable = Unwrap(await controller.Status(tenantId));
            afterEnable.Communication.TenantEnabled.Should().BeTrue();
            afterEnable.AiServices.TenantEnabled.Should().BeTrue();
            afterEnable.Reporting.TenantEnabled.Should().BeFalse();
            afterEnable.StreamData.TenantEnabled.Should().BeFalse();

            // Disable in the Standardized creator's shape: the key is deleted.
            await DeleteAsync(child, TenantCapabilityConfigurationKeys.Communication);

            var afterDisable = Unwrap(await controller.Status(tenantId));
            afterDisable.Communication.TenantEnabled.Should().BeFalse();
            afterDisable.AiServices.TenantEnabled.Should().BeTrue();
        }
        finally
        {
            await DropAsync(systemContext, tenantId);
        }
    }

    [Fact]
    public async Task Status_ReportsTheInstanceKillSwitch_IndependentlyOfTenantFlags()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = await CreateTempChildAsync(systemContext);
        try
        {
            var status = Unwrap(await CreateController(systemContext, instanceEnabled: false).Status(tenantId));

            status.StreamData.InstanceEnabled.Should().BeFalse();
        }
        finally
        {
            await DropAsync(systemContext, tenantId);
        }
    }
}
