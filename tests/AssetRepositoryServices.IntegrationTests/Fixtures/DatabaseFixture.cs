using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MongoDb;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
///     Starts a MongoDB Testcontainer with a replica set (required for transactions).
///
///     Container-bringup pattern matches octo-construction-kit-engine-mongodb /
///     octo-ai-services. Testcontainers' rs.initiate() handshake and mongo's keyfile-init
///     entrypoint race with port binding on CI agents under load (build 34386 hung 40+ min
///     because the temp-mongo's listener hadn't released 27017 when the real mongod tried
///     to bind, exit code 48; the .NET test then hung indefinitely waiting on a dead
///     container). The retry loop with a *fresh* container per attempt + per-attempt hard
///     timeout is the proven fix.
/// </summary>
public class DatabaseFixture : ConfigurationFixture
{
    protected readonly IntegrationTestOptions _options;
    private MongoDbContainer? _mongoDbContainer;

    public DatabaseFixture()
    {
        _options = GetOptions<IntegrationTestOptions>("integrationTest");
    }

    protected override async Task InitializeServicesAsync()
    {
        await Console.Error.WriteLineAsync($"[DatabaseFixture] Starting MongoDB container with image: {_options.MongoDbImage}");
        await Console.Error.FlushAsync();

        const int maxAttempts = 3;
        var perAttemptTimeout = TimeSpan.FromMinutes(2);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            await Console.Error.WriteLineAsync($"[DatabaseFixture] StartAsync attempt {attempt}/{maxAttempts}");
            await Console.Error.FlushAsync();

            // No WithCleanUp(true) — match ck-engine-mongodb / ai-services. WithCleanUp(true)
            // hard-wires Ryuk reaper into the container lifecycle which doesn't get bypassed
            // by TESTCONTAINERS_RYUK_DISABLED, and Ryuk's TCP handshake blocks silently on
            // our self-hosted DinD agent. DisposeServicesAsync calls StopAsync + DisposeAsync
            // explicitly so cleanup guarantee is preserved.
            _mongoDbContainer = new MongoDbBuilder(_options.MongoDbImage)
                .WithReplicaSet()
                .WithName($"mongodb-assetrepo-test-{Guid.NewGuid():N}")
                .WithUsername(_options.AdminUser)
                .WithPassword(_options.AdminUserPassword)
                .Build();

            using var startCts = new CancellationTokenSource(perAttemptTimeout);
            try
            {
                await _mongoDbContainer.StartAsync(startCts.Token);
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $@"Testcontainer MongoDB start failed on attempt {attempt}/{maxAttempts}: {ex.GetType().Name}: {ex.Message}");

                try
                {
                    await _mongoDbContainer.DisposeAsync();
                }
                catch (Exception disposeEx)
                {
                    Console.WriteLine($@"  Disposal of failed container also threw: {disposeEx.Message}");
                }

                _mongoDbContainer = null;

                if (attempt == maxAttempts)
                {
                    throw;
                }

                await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
            }
        }

        var mappedPort = _mongoDbContainer!.GetMappedPublicPort();
        var databaseHost = $"localhost:{mappedPort}";
        Console.WriteLine($@"Using Testcontainer MongoDB at {databaseHost}");

        Services.Configure<OctoSystemConfiguration>(t =>
        {
            t.SystemDatabaseName = SystemDatabaseName;
            t.DatabaseHost = databaseHost;
            t.AdminUser = _options.AdminUser;
            t.AdminUserPassword = _options.AdminUserPassword;
            t.DatabaseUserPassword = _options.DatabaseUserPassword;
            t.UseDirectConnection = true; // For single-node replica set in tests
        });

        await base.InitializeServicesAsync();
    }

    protected override async Task DisposeServicesAsync()
    {
        await Task.Yield();

        if (_mongoDbContainer != null)
        {
            await _mongoDbContainer.StopAsync();
            await _mongoDbContainer.DisposeAsync();
        }
    }

    public string GetConnectionString()
    {
        EnsureInitialized();

        if (_mongoDbContainer is null)
        {
            throw new InvalidOperationException("MongoDB container is not initialized. Call InitializeAsync first.");
        }

        return _mongoDbContainer.GetConnectionString();
    }
}
