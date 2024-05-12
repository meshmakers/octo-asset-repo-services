using Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Services;

/// <summary>
/// Manages the tenants of the application.
/// </summary>
public interface ITenantManager
{
    /// <summary>
    /// Unload a tenant.
    /// </summary>
    /// <param name="tenantId"></param>
    /// <returns></returns>
    Task DisableStreamDataAsync(string tenantId);

    /// <summary>
    /// Enables stream data for a given tenant
    /// </summary>
    /// <param name="tenantId"></param>
    /// <returns></returns>
    Task EnableStreamData(string tenantId);


    /// <summary>
    /// Starts a tenant.
    /// </summary>
    /// <param name="tenantId"></param>
    /// <returns></returns>
    Task StartTenantAsync(string tenantId);

    /// <summary>
    /// Stops a tenant.
    /// </summary>
    /// <param name="tenantId"></param>
    /// <returns></returns>
    Task StopTenantAsync(string tenantId);

    /// <summary>
    /// Deletes a tenant. This will also stop the tenant.
    /// </summary>
    /// <param name="tenantId"></param>
    /// <returns></returns>
    Task DeleteTenantAsync(string tenantId);
    
    /// <summary>
    /// Get the stream data tenant context for a given tenant.
    /// </summary>
    /// <param name="tenantId"></param>
    /// <returns></returns>
    IStreamDataTenantContext? GetStreamDataTenantContext(string tenantId);
}

/// <inheritdoc />
internal class TenantManager : ITenantManager
{
    private const string CommunicationControllerServiceSchemaVersionKey = "CommunicationControllerServices";

    private readonly ILogger<TenantManager> _logger;
    private readonly ISystemContext _systemContext;
    private readonly IStreamDataTenantContextFactory _StreamDataTenantContextFactory;

    private readonly Dictionary<string, IStreamDataTenantContext> _StreamDataTenantContexts = new();
    private readonly SemaphoreSlim _startTenantSemaphore = new(1);


    /// <summary>
    /// c'tor
    /// </summary>
    /// <param name="logger"></param>
    /// <param name="systemContext"></param>
    /// <param name="streamDataTenantContextFactory"></param>
    public TenantManager(ILogger<TenantManager> logger,
        ISystemContext systemContext,
        IStreamDataTenantContextFactory streamDataTenantContextFactory)
    {
        _logger = logger;
        _systemContext = systemContext;
        _StreamDataTenantContextFactory = streamDataTenantContextFactory;
    }

    /// <inheritdoc />
    public async Task DisableStreamDataAsync(string tenantId)
    {
        _logger.LogInformation("Unloading tenant '{TenantId}'", tenantId);

        try
        {
            var tenantContext = await _systemContext.FindTenantContextAsync(tenantId);

            var session = await tenantContext.GetAdminSessionAsync();
            session.StartTransaction();

            await tenantContext.SetConfigurationAsync(session, Constants.StreamDataEnabledKey,
                StreamDataGlobalSettings.Disabled);

            await session.CommitTransactionAsync();

            await StopTenantAsync(tenantId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error unloading tenant '{TenantId}'", tenantId);
        }
    }

    public async Task EnableStreamData(string tenantId)
    {
        if (_StreamDataTenantContexts.TryGetValue(tenantId, out _))
        {
            _logger.LogDebug("Tenant '{TenantId}' is already enabled and started", tenantId);
            return;
        }

        var tenantContext = await _systemContext.FindTenantContextAsync(tenantId);

        // first we check if the tenant has already a stream data configuration
        // this configuration only tells us if stream data is enabled or not, but not if any data is gathered.
        using var session = await tenantContext.GetAdminSessionAsync();
        session.StartTransaction();
        
        var streamDataGlobalSettings =
            await tenantContext.GetConfigurationAsync<StreamDataGlobalSettings>(session, Constants.StreamDataEnabledKey,
                null);

        if (streamDataGlobalSettings is not { IsEnabled: true }) // was never enabled or is disabled
        {
            await tenantContext.SetConfigurationAsync(session, Constants.StreamDataEnabledKey, StreamDataGlobalSettings.Enabled);
          
            await session.CommitTransactionAsync();

            _logger.LogInformation("Enabled stream data for tenant '{TenantId}'", tenantId);
            await StartTenantAsync(tenantId);
            return;
        }

        if (streamDataGlobalSettings.IsEnabled)
        {
            _logger.LogDebug("Tenant '{TenantId}' is already enabled", tenantId);
            await session.CommitTransactionAsync();
        }
    }

    public async Task StartTenantAsync(string tenantId)
    {
        await _startTenantSemaphore.WaitAsync();
        try
        {
            _logger.LogInformation("Starting tenant '{TenantId}'", tenantId);
            if (_StreamDataTenantContexts.TryGetValue(tenantId, out _))
            {
                _logger.LogInformation("Tenant '{TenantId}' is already started", tenantId);
                return;
            }


            var StreamDataContext = await _StreamDataTenantContextFactory.CreateAsync(tenantId);

            if (await StreamDataContext.StartAsync())
            {
                _StreamDataTenantContexts.Add(tenantId, StreamDataContext);

                _logger.LogInformation("Started tenant '{TenantId}'", tenantId);
            }
            else
            {
                _logger.LogInformation("stream data for tenant '{TenantId}' not started", tenantId);
            }
        }
        finally
        {
            _startTenantSemaphore.Release();
        }

    }
    

    public async Task StopTenantAsync(string tenantId)
    {
        if (_StreamDataTenantContexts.Remove(tenantId, out var context))
        {
            _logger.LogInformation("Tenant '{TenantId}' is stopping", tenantId);
            await context.StopAsync();
        }
    }

    public async Task DeleteTenantAsync(string tenantId)
    {
        _logger.LogInformation("Deleting tenant '{TenantId}'", tenantId);
        if (_StreamDataTenantContexts.Remove(tenantId, out var context))
        {
            _logger.LogInformation("Deleting tenant '{TenantId}'", tenantId);
            await context.DeleteAsync();
        }
    }

    public IStreamDataTenantContext? GetStreamDataTenantContext(string tenantId)
    {
        return _StreamDataTenantContexts.GetValueOrDefault(tenantId);
    }
}