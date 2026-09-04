using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MongoDb;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
///     Points the fixture at the process-wide MongoDB replica-set Testcontainer
///     (<see cref="SharedMongoDbContainer" />, AB#5118) and isolates itself on it by name rather
///     than by server: <see cref="ConfigurationFixture.SystemDatabaseName" /> and
///     <see cref="ConfigurationFixture.SystemTenantId" /> are GUID-suffixed per fixture instance.
///     Before AB#5118 every fixture started a MongoDB container of its own because all of them
///     shared one hardcoded system database name.
/// </summary>
public class DatabaseFixture : ConfigurationFixture
{
    protected readonly IntegrationTestOptions _options;
    private MongoDbContainer? _sharedMongoDbContainer;

    public DatabaseFixture()
    {
        _options = GetOptions<IntegrationTestOptions>("integrationTest");
    }

    protected override async Task InitializeServicesAsync()
    {
        _sharedMongoDbContainer = await SharedMongoDbContainer.GetContainerAsync(_options);

        var databaseHost = $"localhost:{_sharedMongoDbContainer.GetMappedPublicPort()}";
        Console.WriteLine($@"Using Testcontainer MongoDB at {databaseHost} with system database {SystemDatabaseName}");

        Services.Configure<OctoSystemConfiguration>(t =>
        {
            t.SystemTenantId = SystemTenantId;
            t.SystemDatabaseName = SystemDatabaseName;
            t.DatabaseHost = databaseHost;
            t.AdminUser = _options.AdminUser;
            t.AdminUserPassword = _options.AdminUserPassword;
            t.DatabaseUserPassword = _options.DatabaseUserPassword;
            t.UseDirectConnection = true; // For single-node replica set in tests
        });

        await base.InitializeServicesAsync();
    }

    protected override Task DisposeServicesAsync()
    {
        // The shared container outlives every individual fixture; SharedContainerLifetime stops it
        // after the last collection. The databases this fixture created go with it, so there is
        // nothing to drop here.
        return Task.CompletedTask;
    }

    public string GetConnectionString()
    {
        EnsureInitialized();

        if (_sharedMongoDbContainer is null)
        {
            throw new InvalidOperationException("MongoDB container is not initialized. Call InitializeAsync first.");
        }

        return _sharedMongoDbContainer.GetConnectionString();
    }
}
