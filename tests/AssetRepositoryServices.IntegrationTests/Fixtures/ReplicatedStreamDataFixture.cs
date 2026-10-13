using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
/// A <see cref="StreamDataFixture"/> on a CrateDB cluster of its own (<see cref="CrateDbTestCluster"/>)
/// whose archive tables are created with one shard and one replica, so that every table has a
/// primary and a replica copy on different nodes (AB#6647). One shard on purpose: all rows of a
/// statement then reach the same shard, which is the condition under which CrateDB before 5.10.12
/// stores the new rows of a mixed upsert without their deterministic column defaults on the replica.
/// </summary>
public class ReplicatedStreamDataFixture : StreamDataFixture
{
    private CrateDbTestCluster? _cluster;

    /// <summary>One connection string per node — a read is served by the copy the node picks.</summary>
    public IReadOnlyList<string> NodeConnectionStrings =>
        _cluster?.ConnectionStrings ?? throw new InvalidOperationException("The CrateDB cluster is not started.");

    /// <summary>Time the cluster took to form, for the test output.</summary>
    public TimeSpan ClusterStartupDuration =>
        _cluster?.StartupDuration ?? throw new InvalidOperationException("The CrateDB cluster is not started.");

    protected override async Task<string> GetCrateDbConnectionStringAsync()
    {
        _cluster = await CrateDbTestCluster.StartAsync();
        return _cluster.ConnectionStrings[0];
    }

    protected override void ConfigureStreamDataServices(IServiceCollection services)
    {
        base.ConfigureStreamDataServices(services);

        // PostConfigure: the base registers its single-node values (0 replicas) as a Configure
        // after this method ran.
        services.PostConfigure<StreamDataConfiguration>(o =>
        {
            o.NumberOfShards = 1;
            o.NumberOfReplicas = 1;
        });
    }

    protected override async Task DisposeServicesAsync()
    {
        try
        {
            await base.DisposeServicesAsync();
        }
        finally
        {
            if (_cluster is not null)
            {
                await _cluster.DisposeAsync();
                _cluster = null;
            }
        }
    }
}
