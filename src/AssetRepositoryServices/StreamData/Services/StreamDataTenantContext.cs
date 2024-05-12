using Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Services;



/// <summary>
/// The StreamDataTenantContext is responsible to manage the lifecycle of a tenant.
/// It imports the construction kit model if not already done and manages the configuration.
/// </summary>
public interface IStreamDataTenantContext
{
    /// <summary>
    /// Starts the tenant
    /// </summary>
    /// <returns></returns>
    Task<bool> StartAsync();

    /// <summary>
    /// Stops the tenant context.
    /// </summary>
    /// <returns></returns>
    Task StopAsync();


    /// <summary>
    /// Deletes the stream data database for the tenant.
    /// </summary>
    /// <returns></returns>
    Task DeleteAsync();
}

internal class StreamDataTenantContext(
    ILoggerFactory loggerFactory,
    ITenantContext context,
    IStreamDataDatabaseManager dataDatabaseManager)
    : IStreamDataTenantContext
{
    private readonly ILogger<StreamDataTenantContext> _logger = loggerFactory.CreateLogger<StreamDataTenantContext>();


    public Task StopAsync()
    {
        _logger.LogInformation("Stopping tenant '{TenantId}'", context.TenantId);
        return Task.CompletedTask;
    }

    public async Task DeleteAsync()
    {
        _logger.LogInformation("Deleting tenant '{TenantId}'", context.TenantId);
        await StopAsync();
        await dataDatabaseManager.DeleteDatabaseAsync(context.TenantId);
    }


    public async Task<bool> StartAsync()
    {
        var tenantId = context.TenantId;

        // either the configuration is null or disabled
        if (!await IsStreamDataEnabled(tenantId))
        {
            _logger.LogInformation("stream data is not enabled for tenant '{TenantId}'", tenantId);

            return false;
        }
        

        // prepare the stream data database
        await dataDatabaseManager.EnsureDatabaseCreated(tenantId);
        

        return true;
    }

    private async Task<bool> IsStreamDataEnabled(string tenantId)
    {
        if (await GetSettings() is { IsEnabled: true })
        {
            return true;
        }

        _logger.LogDebug("stream data not enabled for tenant '{TenantId}'", tenantId);
        return false;
    }

    private async Task<StreamDataGlobalSettings?> GetSettings()
    {
        using var session = await context.GetAdminSessionAsync();
        session.StartTransaction();
        var configuration =
            await context.GetConfigurationAsync<StreamDataGlobalSettings>(session, Constants.StreamDataEnabledKey,
                null);
        await session.CommitTransactionAsync();

        return configuration;
    }
}