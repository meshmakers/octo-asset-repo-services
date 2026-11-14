using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Methods;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
///     AB#8722: the first rollout of the archive CK methods (AB#8598) on a cluster whose system tenant has no System.StreamData.
///     Every replica imports the model into the system tenant (Order 12) and then validates its in-process registrations
///     against the CK cache of that tenant (Order 15). The import unloads a CK cache that lacks the model and nothing loaded it
///     again before the validation, which reads only a loaded cache: the replica stopped with "the CK cache of tenant(s)
///     'octosystem' has no such method" (test-2, 2026-11-10). Each test runs on a system tenant of its own, without
///     System.StreamData, and starts replicas with their own service provider on it.
/// </summary>
public class SystemTenantStreamDataImportReplicaTests
{
    private const int ReplicaCount = 4;

    [Fact]
    public async Task ReplicasStartingTogether_AllPassTheMethodValidation()
    {
        await using var cluster = await AssetRepoFixtureWithoutStreamData.CreateAsync();
        var replicas = await cluster.CreateReplicasAsync(ReplicaCount);
        try
        {
            // all replicas start at once, as the pods of one deployment on the first rollout
            var starts = replicas.Select(r => Task.Run(r.StartAsync)).ToList();
            var all = Task.WhenAll(starts);
            try
            {
                await all;
            }
            catch
            {
                // report every replica that failed, not only the first one
            }

            var failures = starts.Where(t => t.IsFaulted).Select(t => t.Exception!.GetBaseException().Message).ToList();
            Assert.True(failures.Count == 0,
                $"{failures.Count} of {ReplicaCount} replicas failed to start:{Environment.NewLine}" +
                string.Join(Environment.NewLine, failures));
            Assert.True(await cluster.IsStreamDataInstalledAsync());
        }
        finally
        {
            await DisposeAllAsync(replicas);
        }
    }

    [Fact]
    public async Task ReplicaThatImportsTheModel_PassesTheMethodValidation()
    {
        await using var cluster = await AssetRepoFixtureWithoutStreamData.CreateAsync();
        var replicas = await cluster.CreateReplicasAsync(1);
        try
        {
            // the replica loaded the system tenant's cache before the import, as the tenant setup of a start does
            await replicas[0].LoadSystemTenantCkCacheAsync();

            await replicas[0].StartAsync();

            Assert.True(await cluster.IsStreamDataInstalledAsync());
        }
        finally
        {
            await DisposeAllAsync(replicas);
        }
    }

    [Fact]
    public async Task ReplicaWhoseCacheWasLoadedBeforeAnotherReplicaImported_PassesTheMethodValidation()
    {
        await using var cluster = await AssetRepoFixtureWithoutStreamData.CreateAsync();
        var replicas = await cluster.CreateReplicasAsync(2);
        try
        {
            // replica 2 loaded the cache while the model was not installed yet
            await replicas[1].LoadSystemTenantCkCacheAsync();

            // replica 1 imports the model; replica 2 then finds it installed and imports nothing
            await replicas[0].StartAsync();
            await replicas[1].StartAsync();
        }
        finally
        {
            await DisposeAllAsync(replicas);
        }
    }

    [Fact]
    public async Task HandlerOfAMethodTheModelDoesNotHave_StillFailsTheStart()
    {
        // AC 2 of AB#8722: the fix must not hide a registration that is really wrong.
        const string missingMethodId = "System.StreamData/Archive.DoesNotExist-1";
        await using var cluster = await AssetRepoFixtureWithoutStreamData.CreateAsync();
        var replicas = await cluster.CreateReplicasAsync(1,
            b => b.Handle<ArchiveActivateHandler>(missingMethodId));
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(replicas[0].StartAsync);

            Assert.Contains(missingMethodId, ex.Message);
            Assert.Contains("has no such method", ex.Message);
        }
        finally
        {
            await DisposeAllAsync(replicas);
        }
    }

    private static async Task DisposeAllAsync(IEnumerable<AssetRepoReplicaFixture> replicas)
    {
        foreach (var replica in replicas)
        {
            await replica.DisposeAsync();
        }
    }

    /// <summary>A fresh system tenant (and test tenant) without System.StreamData, set up by a fixture that is no replica.</summary>
    private sealed class AssetRepoFixtureWithoutStreamData : AssetRepoFixture
    {
        public static async Task<AssetRepoFixtureWithoutStreamData> CreateAsync()
        {
            var fixture = new AssetRepoFixtureWithoutStreamData { OutputHelper = TestContext.Current.TestOutputHelper };
            await fixture.InitializeAsync();
            Assert.False(await fixture.IsStreamDataInstalledAsync(),
                "precondition: the system tenant of the fixture must not have System.StreamData");
            return fixture;
        }

        public async Task<bool> IsStreamDataInstalledAsync()
        {
            var systemContext = GetSystemContext();
            var tenant = await systemContext.FindTenantContextAsync(systemContext.TenantId);
            return await tenant.IsCkModelSatisfiedAsync(SystemStreamDataCkIds.CkModelId);
        }

        public async Task<List<AssetRepoReplicaFixture>> CreateReplicasAsync(int count,
            Action<Meshmakers.Octo.Services.Methods.ICkMethodHandlerRegistrationBuilder>? configureMethods = null)
        {
            var replicas = new List<AssetRepoReplicaFixture>();
            for (var i = 0; i < count; i++)
            {
                var replica = new AssetRepoReplicaFixture(SystemTenantId, SystemDatabaseName, configureMethods)
                {
                    OutputHelper = TestContext.Current.TestOutputHelper
                };
                await replica.InitializeAsync();
                replicas.Add(replica);
            }

            return replicas;
        }
    }
}
