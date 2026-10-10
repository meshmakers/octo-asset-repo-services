using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files.Migration;

/// <summary>
///     Raw MongoDB access to a tenant database for the file data migration (AB#6175).
/// </summary>
public interface ITenantMongoDatabaseProvider
{
    /// <summary>
    ///     The tenant's database, or null when the tenant does not exist.
    /// </summary>
    Task<IMongoDatabase?> TryGetDatabaseAsync(string tenantId);
}

/// <summary>
///     Builds one admin <see cref="MongoClient" /> from <see cref="OctoSystemConfiguration" /> with the same
///     connection settings the engine's admin client uses (servers, admin credentials, TLS, replica set,
///     majority read/write concern) and hands out tenant databases by name.
///     <para>
///         Why an own client: the sweep must work on the stored documents without the CK cache (the legacy
///         types may already be gone from it) and must reach <c>fs.files</c> and the association collection;
///         the engine keeps its <c>IMongoDatabase</c> internal (<c>IAdminRepositoryAccess</c>,
///         <c>IOctoSessionInternal</c>), so there is no public way to borrow it.
///     </para>
/// </summary>
internal sealed class TenantMongoDatabaseProvider : ITenantMongoDatabaseProvider, IDisposable
{
    private readonly ISystemContext _systemContext;
    private readonly Lazy<MongoClient> _client;

    public TenantMongoDatabaseProvider(ISystemContext systemContext,
        IOptions<OctoSystemConfiguration> systemConfiguration)
    {
        _systemContext = systemContext;
        _client = new Lazy<MongoClient>(() => CreateClient(systemConfiguration.Value));
    }

    public async Task<IMongoDatabase?> TryGetDatabaseAsync(string tenantId)
    {
        var tenantContext = await _systemContext.TryFindTenantContextAsync(tenantId).ConfigureAwait(false);
        return tenantContext == null ? null : _client.Value.GetDatabase(tenantContext.DatabaseName);
    }

    public void Dispose()
    {
        if (_client.IsValueCreated)
        {
            _client.Value.Dispose();
        }
    }

    private static MongoClient CreateClient(OctoSystemConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.AdminUser) ||
            string.IsNullOrWhiteSpace(configuration.AdminUserPassword))
        {
            throw new InvalidOperationException(
                "The files migration needs the MongoDB admin credentials (System:AdminUser / AdminUserPassword).");
        }

        var urlBuilder = new MongoUrlBuilder();
        // Parse, not the string ctor: DatabaseHost may be "host:port" (same as the engine, CSHARP-6171).
        if (configuration.DatabaseHost.Contains(','))
        {
            urlBuilder.Servers = configuration.DatabaseHost.Split(',').Select(MongoServerAddress.Parse);
        }
        else
        {
            urlBuilder.Server = MongoServerAddress.Parse(configuration.DatabaseHost);
        }

        urlBuilder.Username = configuration.AdminUser;
        urlBuilder.Password = configuration.AdminUserPassword;
        urlBuilder.AuthenticationSource = configuration.AuthenticationDatabaseName;
        urlBuilder.ApplicationName = "octo-asset-repo-files-migration";
        urlBuilder.UseTls = configuration.UseTls;
        urlBuilder.AllowInsecureTls = configuration.AllowInsecureTls;
        urlBuilder.RetryReads = true;
        urlBuilder.RetryWrites = true;
        urlBuilder.DirectConnection = configuration.UseDirectConnection;
        if (!string.IsNullOrWhiteSpace(configuration.ReplicaSetName))
        {
            urlBuilder.ReplicaSetName = configuration.ReplicaSetName;
        }

        var settings = MongoClientSettings.FromUrl(urlBuilder.ToMongoUrl());
        settings.ReadConcern = ReadConcern.Majority;
        settings.WriteConcern = new WriteConcern(WriteConcern.WMode.Majority, TimeSpan.FromSeconds(30));
        return new MongoClient(settings);
    }
}
