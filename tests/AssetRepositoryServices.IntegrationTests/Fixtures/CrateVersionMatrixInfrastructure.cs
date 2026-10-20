using System.Net;
using System.Security.Cryptography;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Npgsql;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
///     The Docker side of the CrateDB version matrix (AB#5490): one network, one S3 store (SeaweedFS, the store of the
///     local kind infrastructure) and CrateDB containers of an exact image tag that reach the store by its network alias.
///     A dump written by a 5.x CrateDB is read by a 6.x CrateDB through that store exactly as in a cluster, because the
///     <c>COPY … TO / FROM</c> statements run on the nodes, not in the test.
/// </summary>
/// <remarks>
///     The store credential is generated per process, kept in two fields and never written to a log, a file or an
///     exception text (<see cref="ArchiveCopyStagingLocation" /> redacts it).
/// </remarks>
internal static class CrateVersionMatrixInfrastructure
{
    /// <summary>The environment variable that replaces the image of the 5.9 series (for example another patch release).</summary>
    public const string Crate59Variable = "OCTO_CRATE_MATRIX_IMAGE_59";

    /// <summary>The environment variable that replaces the image of the 5.10 series.</summary>
    public const string Crate510Variable = "OCTO_CRATE_MATRIX_IMAGE_510";

    /// <summary>The environment variable that replaces the image of the 6.x target.</summary>
    public const string Crate6Variable = "OCTO_CRATE_MATRIX_IMAGE_6";

    /// <summary>The 5.9 series: the oldest series a tenant dump comes from (AB#5490). Exact tag, never a floating one.</summary>
    public static string Crate59 => Environment.GetEnvironmentVariable(Crate59Variable) is { Length: > 0 } image ? image : "crate:5.9.10";

    /// <summary>The 5.10 series: the source series of the 5.10 to 6.x step (the other integration tests run on 6.x since AB#6651).</summary>
    public static string Crate510 => Environment.GetEnvironmentVariable(Crate510Variable) is { Length: > 0 } image ? image : "crate:5.10.10";

    /// <summary>The 6.x target of the matrix.</summary>
    public static string Crate6 => Environment.GetEnvironmentVariable(Crate6Variable) is { Length: > 0 } image ? image : "crate:6.4.6";

    private const string SeaweedFsImage = "chrislusf/seaweedfs:4.48";
    private const string Bucket = "octo-artifacts";
    private const string S3Alias = "s3";
    private const int S3Port = 8333;

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static INetwork? _network;
    private static IContainer? _s3;
    private static string? _accessKey;
    private static string? _secretKey;

    /// <summary>
    ///     A staging location in the store as CrateDB sees it (<c>s3://key:secret@s3:8333/bucket/prefix</c>, plain HTTP).
    ///     Call <see cref="EnsureStoreAsync" /> first.
    /// </summary>
    public static ArchiveCopyStagingLocation Location(string prefix)
    {
        if (_accessKey is null || _secretKey is null)
        {
            throw new InvalidOperationException("The S3 store is not started.");
        }

        return new ArchiveCopyStagingLocation(
            $"s3://{Uri.EscapeDataString(_accessKey)}:{Uri.EscapeDataString(_secretKey)}@{S3Alias}:{S3Port}/{Bucket}/{prefix.Trim('/')}",
            new Dictionary<string, string> { ["protocol"] = "http" });
    }

    /// <summary>Starts the network and the S3 store once per process.</summary>
    public static async Task EnsureStoreAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (_s3 is not null)
            {
                return;
            }

            _network = new NetworkBuilder().WithName($"octo-crate-matrix-{Guid.NewGuid():N}").Build();
            await _network.CreateAsync();

            _accessKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            _secretKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            var identities = "{\"identities\":[{\"name\":\"matrix\",\"credentials\":[{\"accessKey\":\"" + _accessKey +
                             "\",\"secretKey\":\"" + _secretKey + "\"}],\"actions\":[\"Admin\",\"Read\",\"Write\",\"List\",\"Tagging\"]}]}";

            // The same arguments as the store of the kind infrastructure (octo-tools kubernetes/infra/s3-store.yaml).
            var s3 = new ContainerBuilder(SeaweedFsImage)
                .WithName($"seaweedfs-crate-matrix-{Guid.NewGuid():N}")
                .WithNetwork(_network)
                .WithNetworkAliases(S3Alias)
                .WithPortBinding(S3Port, true)
                .WithResourceMapping(Encoding.UTF8.GetBytes(identities), "/etc/seaweedfs-s3.json")
                .WithCommand("mini", "-dir=/data", $"-bucket={Bucket}", "-s3.config=/etc/seaweedfs-s3.json",
                    "-webdav=false", "-admin.ui=false", "-s3.port.iceberg=0", "-s3.port.lance=0")
                // The S3 port answers 403 to an unsigned request: that is the readiness signal.
                .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request =>
                    request.ForPort(S3Port).ForStatusCode(HttpStatusCode.Forbidden)))
                .Build();
            await s3.StartAsync();
            _s3 = s3;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    ///     Starts a single-node CrateDB of <paramref name="image" /> on the matrix network. The returned
    ///     <see cref="CrateVersionContainer" /> owns it.
    /// </summary>
    public static async Task<CrateVersionContainer> StartCrateAsync(string image)
    {
        await EnsureStoreAsync();
        // A registry-qualified reference contains '/' and ':', which a Docker container name does not allow.
        var imageName = global::System.Text.RegularExpressions.Regex.Replace(image, "[^a-zA-Z0-9_.-]", "-");
        var container = new ContainerBuilder(image)
            .WithName($"cratedb-matrix-{imageName}-{Guid.NewGuid():N}")
            .WithNetwork(_network!)
            .WithPortBinding(5432, true)
            .WithPortBinding(4200, true)
            .WithEnvironment("CRATE_HEAP_SIZE", "1g")
            .WithCommand("-Cdiscovery.type=single-node")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("started"))
            .Build();
        try
        {
            await container.StartAsync();
        }
        catch
        {
            try
            {
                await container.DisposeAsync();
            }
            catch (Exception disposeEx)
            {
                Console.WriteLine($@"  Disposal of the failed CrateDB container also threw: {disposeEx.Message}");
            }

            throw;
        }

        var connectionString =
            $"Host=localhost;Port={container.GetMappedPublicPort(5432)};Username=crate;SSL Mode=Prefer";

        // The staging copy refuses to run unless COPY statements are kept out of sys.jobs_log (their text carries
        // the storage credential); production sets the same filter on the cluster (AB#6944).
        await using (var conn = new NpgsqlConnection(connectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SET GLOBAL TRANSIENT \"stats.jobs_log_filter\" = 'stmt NOT ILIKE ''copy %'''", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        return new CrateVersionContainer(image, container, connectionString);
    }

    /// <summary>Stops the store and the network; a no-op when the matrix never ran.</summary>
    public static async Task DisposeAsync()
    {
        await Gate.WaitAsync();
        try
        {
            var s3 = _s3;
            var network = _network;
            _s3 = null;
            _network = null;
            _accessKey = null;
            _secretKey = null;
            try
            {
                if (s3 is not null)
                {
                    await s3.DisposeAsync();
                }
            }
            finally
            {
                if (network is not null)
                {
                    await network.DisposeAsync();
                }
            }
        }
        finally
        {
            Gate.Release();
        }
    }
}

/// <summary>A CrateDB container of the matrix: its image, connection string and teardown.</summary>
internal sealed class CrateVersionContainer(string image, IContainer container, string connectionString)
    : IAsyncDisposable
{
    public string Image { get; } = image;

    public string ConnectionString { get; } = connectionString;

    public async ValueTask DisposeAsync()
    {
        try
        {
            await container.StopAsync();
        }
        finally
        {
            await container.DisposeAsync();
        }
    }
}
