using Meshmakers.Octo.Runtime.Contracts.MongoDb;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Services;

/// <summary>
/// Factory to create stream data tenant contexts.
/// </summary>
internal interface IStreamDataTenantContextFactory
{
    /// <summary>
    /// Creates a stream data tenant context.
    /// </summary>
    /// <param name="tenantId"></param>
    /// <returns></returns>
    Task<IStreamDataTenantContext> CreateAsync(string tenantId);
}

/// <inheritdoc />
internal class StreamDataTenantContextFactory : IStreamDataTenantContextFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ISystemContext _systemContext;
    private readonly IStreamDataDatabaseManager _dataDatabaseManager;
    private readonly ILogger<StreamDataTenantContextFactory> _logger;

    /// <summary>
    /// ctor    
    /// </summary>
    /// <param name="loggerFactory"></param>
    /// <param name="systemContext"></param>
    /// <param name="dataDatabaseManager"></param>
    public StreamDataTenantContextFactory(
        ILoggerFactory loggerFactory,
        ISystemContext systemContext,
        IStreamDataDatabaseManager dataDatabaseManager)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<StreamDataTenantContextFactory>();
        _systemContext = systemContext;
        _dataDatabaseManager = dataDatabaseManager;
    }


    /// <inheritdoc />
    public async Task<IStreamDataTenantContext> CreateAsync(string tenantId)
    {
        _logger.LogDebug("Creating tenant context for tenant '{TenantId}'", tenantId);

        var tenantContext = await _systemContext.FindTenantContextAsync(tenantId);

        return new StreamDataTenantContext(_loggerFactory, tenantContext, _dataDatabaseManager);
    }
}