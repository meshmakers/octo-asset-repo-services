
using Meshmakers.Octo.StreamData;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Services;

/// <summary>
/// Responsible for managing the stream data database
/// </summary>
internal interface IStreamDataDatabaseManager
{
    /// <summary>
    /// Creates the stream data database for the tenant.
    /// </summary>
    /// <param name="tenantId"></param>
    /// <returns></returns>
    Task EnsureDatabaseCreated(string tenantId);


    /// <summary>
    /// Deletes the stream data database for the tenant.
    /// </summary>
    /// <param name="tenantId"></param>
    /// <returns></returns>
    Task DeleteDatabaseAsync(string tenantId);
}


/// <summary>
/// Represents a service that is responsible for querying data.
/// </summary>


internal class StreamDataDatabaseManager : IStreamDataDatabaseManager
{
    private readonly ILogger<StreamDataDatabaseManager> _logger;
    private readonly IStreamDataDatabaseClient _databaseClient;
    private readonly IStreamDataDatabaseManagementClient _databaseManagementClient;

    public StreamDataDatabaseManager(ILogger<StreamDataDatabaseManager> logger,
        IStreamDataDatabaseClient databaseClient, 
        IStreamDataDatabaseManagementClient databaseManagementClient)
    {
        _logger = logger;
        _databaseClient = databaseClient;
        _databaseManagementClient = databaseManagementClient;
    }

    public async Task EnsureDatabaseCreated(string tenantId)
    {
        await _databaseManagementClient.CreateStreamDataTableIfNotExistAsync(tenantId);
    }

    public async Task DeleteDatabaseAsync(string tenantId)
    {
        await _databaseManagementClient.DeleteStreamDataDatabaseAsync(tenantId);
    }
}