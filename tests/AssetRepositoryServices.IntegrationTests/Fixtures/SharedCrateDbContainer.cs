using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
///     One CrateDB Testcontainer for the whole test process instead of one per
///     <see cref="StreamDataFixture" /> instance (AB#5118). CrateDB isolates stream data by schema:
///     <c>TenantSchema.SchemaName</c> derives the schema from the tenant id, and every fixture now
///     runs under its own <see cref="ConfigurationFixture.SystemTenantId" /> (GUID-suffixed), so the
///     three stream-data collections land in three separate schemas on the one shared cluster.
///     Archive tables are additionally named after the archive's runtime id, which is unique per
///     fixture anyway.
///
///     Teardown is explicit via <see cref="SharedContainerLifetime" />, matching
///     <see cref="SharedMongoDbContainer" />.
/// </summary>
internal static class SharedCrateDbContainer
{
    private const string Image = "crate:5.10.10";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static IContainer? _container;
    private static string? _connectionString;

    public static async Task<string> GetConnectionStringAsync()
    {
        if (_connectionString != null)
        {
            return _connectionString;
        }

        await Gate.WaitAsync();
        try
        {
            if (_connectionString != null)
            {
                return _connectionString;
            }

            await Console.Error.WriteLineAsync($"[SharedCrateDbContainer] Starting CrateDB container with image: {Image}");
            await Console.Error.FlushAsync();

            // Single-node cluster. The heap is larger than the former per-fixture 512m because this
            // one node now serves every stream-data collection instead of one each.
            var container = new ContainerBuilder(Image)
                .WithName($"cratedb-assetrepo-test-shared-{Guid.NewGuid():N}")
                .WithPortBinding(5432, true)
                .WithPortBinding(4200, true)
                .WithEnvironment("CRATE_HEAP_SIZE", "1g")
                .WithCommand("-Cdiscovery.type=single-node")
                .WithWaitStrategy(Wait.ForUnixContainer()
                    .UntilMessageIsLogged("started"))
                .Build();

            await container.StartAsync();

            _container = container;
            _connectionString =
                $"Host=localhost;Port={container.GetMappedPublicPort(5432)};Username=crate;SSL Mode=Prefer";
            Console.WriteLine($@"Using shared Testcontainer CrateDB at {_connectionString}");

            return _connectionString;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    ///     Stops and removes the shared container. Called once by <see cref="SharedContainerLifetime" />
    ///     after every collection has finished; a no-op when no fixture ever needed CrateDB.
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
            _connectionString = null;
        }
        finally
        {
            Gate.Release();
        }
    }
}
