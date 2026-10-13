using System.Diagnostics;
using System.Globalization;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Npgsql;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
///     A CrateDB cluster of <see cref="NodeCount" /> nodes on a Docker network of its own, for the
///     tests that need a table with a <b>replica copy</b> (AB#6647). The process-wide
///     <see cref="SharedCrateDbContainer" /> is a single node: a replica is never assigned there, so
///     anything that only goes wrong on the replica copy cannot show.
///
///     Two nodes are the minimum for one primary and one replica; a third would add memory and
///     start-up time without adding a case. The image is the one of the shared container.
///     Both nodes are master-eligible and each waits for the other, so the cluster only forms
///     with both — which is what a test of the replica copy needs anyway.
///
///     Owned by the fixture that starts it, not by <see cref="SharedContainerLifetime" />: it is
///     stopped when that fixture is disposed, so its memory is only held while its collection runs.
/// </summary>
internal sealed class CrateDbTestCluster : IAsyncDisposable
{
    public const int NodeCount = 2;

    private const string Image = "crate:5.10.10";
    private const string HeapSize = "512m";
    private static readonly TimeSpan FormationTimeout = TimeSpan.FromMinutes(2);

    private readonly INetwork _network;
    private readonly List<IContainer> _nodes = [];

    private CrateDbTestCluster(INetwork network)
    {
        _network = network;
    }

    /// <summary>One connection string per node, in node order.</summary>
    public IReadOnlyList<string> ConnectionStrings { get; private set; } = [];

    /// <summary>Time from the first container start until every node had joined.</summary>
    public TimeSpan StartupDuration { get; private set; }

    public static async Task<CrateDbTestCluster> StartAsync()
    {
        var id = Guid.NewGuid().ToString("N");
        var network = new NetworkBuilder().WithName($"cratedb-assetrepo-test-cluster-{id}").Build();
        var cluster = new CrateDbTestCluster(network);
        try
        {
            await cluster.StartNodesAsync(id);
        }
        catch
        {
            // Nothing else holds a reference to a cluster that never formed.
            await cluster.DisposeAsync();
            throw;
        }

        return cluster;
    }

    private async Task StartNodesAsync(string id)
    {
        await Console.Error.WriteLineAsync(
            $"[CrateDbTestCluster] Starting {NodeCount} CrateDB nodes with image: {Image}");
        await Console.Error.FlushAsync();

        var stopwatch = Stopwatch.StartNew();
        await _network.CreateAsync();

        var nodeNames = Enumerable.Range(1, NodeCount).Select(i => $"crate{i}").ToList();
        var seedHosts = string.Join(",", nodeNames);
        foreach (var nodeName in nodeNames)
        {
            _nodes.Add(new ContainerBuilder(Image)
                .WithName($"cratedb-assetrepo-test-cluster-{id}-{nodeName}")
                .WithNetwork(_network)
                .WithNetworkAliases(nodeName)
                .WithPortBinding(5432, true)
                .WithEnvironment("CRATE_HEAP_SIZE", HeapSize)
                .WithCommand(
                    $"-Ccluster.name=assetrepo-test-{id}",
                    $"-Cnode.name={nodeName}",
                    "-Cnetwork.host=_site_",
                    $"-Cdiscovery.seed_hosts={seedHosts}",
                    $"-Ccluster.initial_master_nodes={seedHosts}",
                    $"-Cgateway.expected_data_nodes={NodeCount.ToString(CultureInfo.InvariantCulture)}",
                    $"-Cgateway.recover_after_data_nodes={NodeCount.ToString(CultureInfo.InvariantCulture)}",
                    // A node that binds to a non-loopback address enforces the bootstrap checks, and
                    // the vm.max_map_count one depends on the Docker host. The test tables are tiny;
                    // without mmap the check does not apply.
                    "-Cnode.store.allow_mmap=false")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("started"))
                .Build());
        }

        await Task.WhenAll(_nodes.Select(n => n.StartAsync()));

        ConnectionStrings = _nodes
            .Select(n => $"Host=localhost;Port={n.GetMappedPublicPort(5432)};Username=crate;SSL Mode=Prefer")
            .ToList();

        await WaitForAllNodesAsync();
        stopwatch.Stop();
        StartupDuration = stopwatch.Elapsed;

        Console.WriteLine(
            $@"Using Testcontainer CrateDB cluster of {NodeCount} nodes at {string.Join(" | ", ConnectionStrings)} " +
            $@"(heap {HeapSize} each, formed in {StartupDuration.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)} s)");
    }

    /// <summary>
    ///     "started" in a node's log says the node is up, not that it found the other one. Every node
    ///     has to see all of them before a table with a replica can get that replica assigned.
    /// </summary>
    private async Task WaitForAllNodesAsync()
    {
        var deadline = Stopwatch.StartNew();
        foreach (var connectionString in ConnectionStrings)
        {
            while (true)
            {
                try
                {
                    await using var connection = new NpgsqlConnection(connectionString);
                    await connection.OpenAsync();
                    await using var command = new NpgsqlCommand("SELECT COUNT(*) FROM sys.nodes", connection);
                    var nodes = Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
                    if (nodes == NodeCount)
                    {
                        break;
                    }
                }
                catch (Exception) when (deadline.Elapsed < FormationTimeout)
                {
                    // The node does not answer SQL before the cluster has a master.
                }

                if (deadline.Elapsed >= FormationTimeout)
                {
                    throw new TimeoutException(
                        $"The CrateDB test cluster did not form within {FormationTimeout.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)} s.");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500));
            }
        }
    }

    /// <summary>
    ///     Stops and removes every node and the network. Each step runs even when an earlier one
    ///     faults, so a failed stop cannot strand the other node or the network on the agent.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        var failures = new List<Exception>();
        foreach (var node in _nodes)
        {
            try
            {
                await node.DisposeAsync();
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        _nodes.Clear();

        try
        {
            await _network.DeleteAsync();
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("Disposal of the CrateDB test cluster failed.", failures);
        }
    }
}
