using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Tenant;

/// <summary>
///     AB#4255 — the delete/detach guard reads the capability flags exactly as the owning services
///     persist them. These tests write the flags through the same engine calls the owning code uses
///     (<c>SetConfigurationAsync</c> with the Standardized creator's value shape, the engine's
///     <c>DisableStreamDataAsync</c>, <c>DeleteConfigurationAsync</c> as in <c>DisableAsync</c>) and
///     check that the reader sees them, against a real MongoDB.
/// </summary>
[Collection(AssetRepoCollection.Name)]
public class TenantCapabilityStateReaderTests(AssetRepoFixture fixture)
{
    private readonly TenantCapabilityStateReader _reader = new(NullLogger<TenantCapabilityStateReader>.Instance);

    private static async Task<string> CreateTempChildAsync(ISystemContext systemContext)
    {
        var tenantId = $"temp-cap-{Guid.NewGuid():N}";
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
    public async Task Reader_SeesTheStreamDataFlag_WrittenAndClearedByTheEngine()
    {
        // The child overload is used on purpose: resolving the child through the parent would run
        // EnsureStreamDataCkModelIfEnabledAsync against a fixture without the stream data stack.
        var systemContext = fixture.GetSystemContext();
        var tenantId = await CreateTempChildAsync(systemContext);
        try
        {
            var child = await GetChildAsync(systemContext, tenantId);

            (await _reader.GetEnabledCapabilitiesAsync(child)).Should().BeEmpty();

            // What EnableStreamDataAsync writes (it is gated on the instance-level StreamData:Enabled).
            await SetAsync(child, StreamDataConfigurationKeys.StreamDataEnabledKey, StreamDataGlobalSettings.Enabled);
            (await _reader.GetEnabledCapabilitiesAsync(child)).Should().Equal(TenantCapability.StreamData);

            // The real Disable keeps the key with IsEnabled = false - must read as disabled.
            await child.DisableStreamDataAsync();
            (await _reader.GetEnabledCapabilitiesAsync(child)).Should().BeEmpty();
        }
        finally
        {
            await DropAsync(systemContext, tenantId);
        }
    }

    [Fact]
    public async Task Reader_SeesCommunicationReportingAndAiFlags_InTheStandardizedCreatorShape()
    {
        // The statement below is the one DefaultConfigurationCreatorServiceStandardized.EnableAsync
        // runs under the owning service's key; DeleteConfigurationAsync is what its DisableAsync does.
        // Read through the parent overload, i.e. the path the tenant Delete/Detach guard takes.
        var systemContext = fixture.GetSystemContext();
        var tenantId = await CreateTempChildAsync(systemContext);
        try
        {
            var child = await GetChildAsync(systemContext, tenantId);
            var enabled = new DefaultConfigurationEnabled { IsEnabled = true };
            await SetAsync(child, TenantCapabilityConfigurationKeys.Communication, enabled);
            await SetAsync(child, TenantCapabilityConfigurationKeys.Reporting, enabled);
            await SetAsync(child, TenantCapabilityConfigurationKeys.AiServices, enabled);

            (await _reader.GetEnabledCapabilitiesAsync(systemContext, tenantId)).Should().Equal(
                TenantCapability.Communication, TenantCapability.Reporting, TenantCapability.AiServices);

            await DeleteAsync(child, TenantCapabilityConfigurationKeys.Reporting);
            (await _reader.GetEnabledCapabilitiesAsync(systemContext, tenantId)).Should().Equal(
                TenantCapability.Communication, TenantCapability.AiServices);

            await DeleteAsync(child, TenantCapabilityConfigurationKeys.Communication);
            await DeleteAsync(child, TenantCapabilityConfigurationKeys.AiServices);
            (await _reader.GetEnabledCapabilitiesAsync(systemContext, tenantId)).Should().BeEmpty();
        }
        finally
        {
            await DropAsync(systemContext, tenantId);
        }
    }

    [Fact]
    public async Task Reader_ThrowsTenantNotFound_ForATenantThatIsNotAChildOfTheParent()
    {
        var systemContext = fixture.GetSystemContext();

        var act = () => _reader.GetEnabledCapabilitiesAsync(systemContext, $"no-such-{Guid.NewGuid():N}");

        (await act.Should().ThrowAsync<TenantException>()).Which.IsTenantNotFound.Should().BeTrue();
    }
}
