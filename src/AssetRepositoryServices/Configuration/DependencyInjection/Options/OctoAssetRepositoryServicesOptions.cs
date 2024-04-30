// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global

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
        StreamDataConnectionString = "Host=127.0.0.1;Username=crate;SSL Mode=Prefer";
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
    /// (Public) connection string to the stream data database
    /// </summary>
    public string StreamDataConnectionString { get; set; }
}