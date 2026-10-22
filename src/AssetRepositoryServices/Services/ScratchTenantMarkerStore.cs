using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services;

/// <summary>
///     Reads and writes the <see cref="ScratchTenantMarker" /> of a scratch tenant (AB#7192) in the configuration of the
///     system tenant, keyed by the tenant id. See the marker for why it lives there and not in the tenant's own database.
/// </summary>
public interface IScratchTenantMarkerStore
{
    /// <summary>The marker of the tenant, or <c>null</c> when it is not a scratch tenant.</summary>
    Task<ScratchTenantMarker?> GetAsync(string tenantId);

    /// <summary>Records <paramref name="marker" /> for the tenant, replacing a previous one.</summary>
    Task SetAsync(string tenantId, ScratchTenantMarker marker);

    /// <summary>Removes the marker of the tenant; a no-op when there is none.</summary>
    Task DeleteAsync(string tenantId);
}

/// <inheritdoc />
internal sealed class ScratchTenantMarkerStore(IOctoService octoService) : IScratchTenantMarkerStore
{
    /// <inheritdoc />
    public async Task<ScratchTenantMarker?> GetAsync(string tenantId)
    {
        var systemContext = octoService.SystemContext;
        using var session = await systemContext.GetAdminSessionAsync();
        return await systemContext.GetConfigurationAsync<ScratchTenantMarker>(session,
            ScratchTenantMarker.GetConfigurationKey(tenantId), defaultValue: null);
    }

    /// <inheritdoc />
    public async Task SetAsync(string tenantId, ScratchTenantMarker marker)
    {
        var systemContext = octoService.SystemContext;
        using var session = await systemContext.GetAdminSessionAsync();
        await systemContext.SetConfigurationAsync(session, ScratchTenantMarker.GetConfigurationKey(tenantId), marker);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string tenantId)
    {
        var systemContext = octoService.SystemContext;
        using var session = await systemContext.GetAdminSessionAsync();
        await systemContext.DeleteConfigurationAsync(session, ScratchTenantMarker.GetConfigurationKey(tenantId));
    }
}
