// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global

using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration.DependencyInjection.Options;

/// <summary>
///     General Object Service options
/// </summary>
public class OctoAssetRepositoryServicesOptions
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public OctoAssetRepositoryServicesOptions()
    {
        JobDatabaseName = "OctoSystemJobs";
        PrepareJobSchemaIfNecessary = true;
        Authority = "https://localhost:5003";
        PublicUrl = "https://localhost:5001";
        PublicAdminPanelUrl = "https://localhost:5005";
        BrokerHost = "localhost";
        StreamDataUser = "crate";
        StreamDataHost = "127.0.0.1";
#if DEBUGL || DEBUG
        MinLogLevel = LogLevelDto.Trace;
#else
        MinLogLevel = LogLevelDto.Warn;
#endif
    }

    /// <summary>
    ///     Gets or sets the RabbitMq host name
    /// </summary>
    public string BrokerHost { get; set; }

    /// <summary>
    ///     Gets or sets the RabbitMq user
    /// </summary>
    public string? BrokerUser { get; set; }

    /// <summary>
    ///     Gets or sets the RabbitMq password
    /// </summary>
    public string? BrokerPassword { get; set; }

    /// <summary>
    ///     URL of arango db
    /// </summary>
    public string JobDatabaseName { get; set; }

    /// <summary>
    ///     When true, the collections of arango db job database are created when they do not exist
    /// </summary>
    public bool PrepareJobSchemaIfNecessary { get; set; }

    /// <summary>
    ///     (Public) base address of the CAS (Central Authorization Services)
    /// </summary>
    public string Authority { get; set; }

    /// <summary>
    ///     (public) base address of the public URI
    /// </summary>
    public string PublicUrl { get; set; }

    /// <summary>
    ///     (public) base address of the dashboard
    /// </summary>
    public string PublicAdminPanelUrl { get; set; }
    
    /// <summary>
    /// Hostname of crate db server
    /// </summary>
    public string StreamDataHost { get; set; }
    
    /// <summary>
    /// User of crate db
    /// </summary>
    public string StreamDataUser { get; set; }
    
    /// <summary>
    /// Password for crate db
    /// </summary>
    public string? StreamDataPassword { get; set; }
        
    /// <summary>
    /// Gets or sets the minimal log level to be logged
    /// </summary>
    public LogLevelDto MinLogLevel { get; set; }
}