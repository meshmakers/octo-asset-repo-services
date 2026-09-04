using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Xunit;

[assembly: AssemblyFixture(typeof(SharedContainerLifetime))]

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
///     Owns the teardown of the process-wide Testcontainers (AB#5118). The containers are started
///     lazily by the first fixture that needs them and outlive every individual fixture, so nobody
///     else is in a position to stop them; xUnit disposes an assembly fixture after the last
///     collection has finished, which is exactly that point.
///
///     This is the one deliberate deviation from the AB#5116 reference, which lets Testcontainers'
///     Ryuk reaper collect the shared container: this repo avoids Ryuk on purpose because its TCP
///     handshake blocks silently on our self-hosted DinD agent, and the per-fixture containers it
///     replaces were stopped explicitly too.
/// </summary>
public sealed class SharedContainerLifetime : IAsyncLifetime
{
    public ValueTask InitializeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        // Both teardowns run even if the first one faults, so a failing CrateDB cleanup cannot
        // strand the MongoDB container; the CrateDB failure still propagates.
        try
        {
            await SharedCrateDbContainer.DisposeAsync();
        }
        finally
        {
            await SharedMongoDbContainer.DisposeAsync();
        }
    }
}
