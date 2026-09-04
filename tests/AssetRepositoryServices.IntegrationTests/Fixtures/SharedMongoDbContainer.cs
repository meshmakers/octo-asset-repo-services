using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;
using Testcontainers.MongoDb;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
///     One MongoDB replica-set Testcontainer for the whole test process instead of one per fixture
///     (AB#5118; same fix as AB#5116 on octo-construction-kit-engine-mongodb). Every
///     <see cref="DatabaseFixture" />-derived fixture now gets its own
///     <see cref="ConfigurationFixture.SystemDatabaseName" /> (GUID-suffixed), so fixtures no longer
///     need a private server to avoid colliding on the same database - they share the one server
///     this class starts on first use.
///
///     Unlike the sibling repo this container is not left to Testcontainers' Ryuk reaper:
///     <see cref="SharedContainerLifetime" /> stops it after the last collection, which keeps the
///     explicit-teardown guarantee the former per-fixture container had (Ryuk's TCP handshake blocks
///     silently on our self-hosted DinD agent).
/// </summary>
internal static class SharedMongoDbContainer
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static MongoDbContainer? _container;

    public static async Task<MongoDbContainer> GetContainerAsync(IntegrationTestOptions options)
    {
        if (_container != null)
        {
            return _container;
        }

        await Gate.WaitAsync();
        try
        {
            if (_container != null)
            {
                return _container;
            }

            await Console.Error.WriteLineAsync(
                $"[SharedMongoDbContainer] Starting MongoDB container with image: {options.MongoDbImage}");
            await Console.Error.FlushAsync();

            // Same retry rationale as the former per-fixture start: Testcontainers' rs.initiate()
            // handshake and mongo's keyfile-init entrypoint race with port binding on CI agents
            // under load (build 34386 hung 40+ min because the temp-mongo's listener hadn't released
            // 27017 when the real mongod tried to bind, exit code 48; the .NET test then hung
            // indefinitely waiting on a dead container). The retry loop with a *fresh* container per
            // attempt + per-attempt hard timeout is the proven fix.
            const int maxAttempts = 3;
            var perAttemptTimeout = TimeSpan.FromMinutes(2);

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                // No WithCleanUp(true) - see the class remarks: teardown is explicit.
                var container = new MongoDbBuilder(options.MongoDbImage)
                    .WithReplicaSet()
                    .WithName($"mongodb-assetrepo-test-shared-{Guid.NewGuid():N}")
                    .WithUsername(options.AdminUser)
                    .WithPassword(options.AdminUserPassword)
                    .Build();

                using var startCts = new CancellationTokenSource(perAttemptTimeout);
                try
                {
                    await container.StartAsync(startCts.Token);
                    await RaiseTransactionLifetimeLimitAsync(container);
                    _container = container;
                    Console.WriteLine(
                        $@"Using shared Testcontainer MongoDB at localhost:{container.GetMappedPublicPort()}");
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $@"Shared testcontainer MongoDB start failed on attempt {attempt}/{maxAttempts}: {ex.GetType().Name}: {ex.Message}");

                    try
                    {
                        await container.DisposeAsync();
                    }
                    catch (Exception disposeEx)
                    {
                        Console.WriteLine($@"  Disposal of failed container also threw: {disposeEx.Message}");
                    }

                    if (attempt == maxAttempts)
                    {
                        throw;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
                }
            }

            return _container!;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    ///     Raises mongod's transaction lifetime limit above its 60 s default. Since AB#5118 all ten
    ///     collection fixtures bootstrap in parallel against this one server, and each of them wraps
    ///     its whole test-tenant creation - database, database user and the system CK model DDL - in
    ///     a single transaction. On a loaded agent one of those can outlive 60 s, at which point
    ///     mongod aborts it server-side and the fixture fails with "Transaction ... has been
    ///     aborted" (observed once locally while a second integration suite saturated the Docker
    ///     daemon). Best effort: a server that refuses the parameter is left as it is.
    /// </summary>
    private static async Task RaiseTransactionLifetimeLimitAsync(MongoDbContainer container)
    {
        try
        {
            var client = new MongoClient(container.GetConnectionString());
            await client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument
            {
                { "setParameter", 1 }, { "transactionLifetimeLimitSeconds", 900 }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $@"Could not raise transactionLifetimeLimitSeconds on the shared MongoDB: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    ///     Stops and removes the shared container. Called once by <see cref="SharedContainerLifetime" />
    ///     after every collection has finished; a no-op when no fixture ever needed MongoDB.
    /// </summary>
    public static async Task DisposeAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (_container == null)
            {
                return;
            }

            await _container.StopAsync();
            await _container.DisposeAsync();
            _container = null;
        }
        finally
        {
            Gate.Release();
        }
    }
}
