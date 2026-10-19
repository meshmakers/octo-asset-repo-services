using System.Collections.Concurrent;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files.OrphanSweep;

/// <summary>
///     Count report of one orphan sweep run of a tenant (AB#6541). Counts, sizes and durations only: no file names,
///     no ids, no content.
/// </summary>
public sealed record GridFsOrphanSweepReportDto
{
    /// <summary>The tenant that was swept.</summary>
    public required string TenantId { get; init; }

    /// <summary><c>dry-run</c> (nothing was deleted) or <c>remove</c>.</summary>
    public required string Mode { get; init; }

    /// <summary>Applied grace period in minutes: only files older than this were examined.</summary>
    public double GracePeriodMinutes { get; init; }

    /// <summary>FileSystem files older than the grace period that were examined.</summary>
    public long ExaminedFiles { get; init; }

    /// <summary>Examined files that an entity references (never touched).</summary>
    public long ReferencedFiles { get; init; }

    /// <summary>FileSystem files younger than the grace period (not examined).</summary>
    public long YoungFiles { get; init; }

    /// <summary>Orphaned files found (in remove mode: the ones that were targeted).</summary>
    public long OrphanFiles { get; init; }

    /// <summary>GridFS chunks that belong to the orphaned files.</summary>
    public long OrphanChunks { get; init; }

    /// <summary>Orphaned files deleted by this run; always 0 for a dry run.</summary>
    public long RemovedFiles { get; init; }

    /// <summary>Total size of the orphaned files in bytes.</summary>
    public long OrphanBytes { get; init; }

    /// <summary>Duration of the sweep in milliseconds.</summary>
    public long DurationMilliseconds { get; init; }

    /// <summary>UTC time the run finished.</summary>
    public DateTime FinishedUtc { get; init; }
}

/// <summary>
///     Raised when an orphan sweep of the same tenant is already running on this instance.
/// </summary>
public sealed class GridFsOrphanSweepBusyException(string tenantId)
    : InvalidOperationException($"An orphan sweep of tenant '{tenantId}' is already running.");

/// <summary>
///     Runs the engine's <see cref="GridFsOrphanSweeper" /> for one tenant (AB#6541).
/// </summary>
public interface IGridFsOrphanSweepService
{
    /// <summary>
    ///     Sweeps the tenant. Returns null when the tenant does not exist. Throws
    ///     <see cref="ArgumentOutOfRangeException" /> for a grace period below
    ///     <see cref="GridFsOrphanSweepOptions.MinimumGracePeriod" /> and
    ///     <see cref="GridFsOrphanSweepBusyException" /> when the tenant is already being swept.
    /// </summary>
    Task<GridFsOrphanSweepReportDto?> SweepAsync(string tenantId, bool remove, TimeSpan gracePeriod,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     Runs one sweep on the tenant database; the seam between the service and the engine (tests replace it only
///     where a real database is not wanted).
/// </summary>
public interface IGridFsOrphanSweepExecutor
{
    /// <summary>Sweeps the given tenant database.</summary>
    Task<GridFsOrphanSweepResult> SweepAsync(string databaseName, GridFsOrphanSweepOptions options,
        CancellationToken cancellationToken);
}

/// <summary>
///     Engine-backed executor. The engine's user repository access is internal, so the sweeper gets a client of
///     its own: one lazily created admin client (admin credentials of <see cref="OctoSystemConfiguration" />, like
///     <see cref="TenantMongoDatabaseProvider" />), shared by all runs and disposed with the service.
/// </summary>
internal sealed class GridFsOrphanSweepExecutor : IGridFsOrphanSweepExecutor, IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceProvider _serviceProvider;
    private readonly IOptions<OctoSystemConfiguration> _systemConfiguration;
    private readonly Lazy<AdminMongoRepositoryClient> _client;

    public GridFsOrphanSweepExecutor(IServiceProvider serviceProvider, ILoggerFactory loggerFactory,
        IOptions<OctoSystemConfiguration> systemConfiguration)
    {
        _serviceProvider = serviceProvider;
        _loggerFactory = loggerFactory;
        _systemConfiguration = systemConfiguration;
        _client = new Lazy<AdminMongoRepositoryClient>(() => new AdminMongoRepositoryClient(
            loggerFactory.CreateLogger<AdminMongoRepositoryClient>(), _systemConfiguration, _serviceProvider,
            _systemConfiguration.Value.SystemDatabaseName));
    }

    public Task<GridFsOrphanSweepResult> SweepAsync(string databaseName, GridFsOrphanSweepOptions options,
        CancellationToken cancellationToken)
    {
        var sweeper = new GridFsOrphanSweeper(_client.Value, databaseName,
            _loggerFactory.CreateLogger<GridFsOrphanSweeper>());
        return sweeper.SweepAsync(options, cancellationToken);
    }

    public void Dispose()
    {
        if (_client.IsValueCreated)
        {
            _client.Value.Dispose();
        }
    }
}

/// <inheritdoc />
public sealed class GridFsOrphanSweepService : IGridFsOrphanSweepService
{
    private readonly ISystemContext _systemContext;
    private readonly IGridFsOrphanSweepExecutor _executor;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Constructor.</summary>
    public GridFsOrphanSweepService(ISystemContext systemContext, IGridFsOrphanSweepExecutor executor)
    {
        _systemContext = systemContext;
        _executor = executor;
    }

    /// <inheritdoc />
    public async Task<GridFsOrphanSweepReportDto?> SweepAsync(string tenantId, bool remove, TimeSpan gracePeriod,
        CancellationToken cancellationToken = default)
    {
        if (gracePeriod < GridFsOrphanSweepOptions.MinimumGracePeriod)
        {
            throw new ArgumentOutOfRangeException(nameof(gracePeriod),
                $"The grace period must be at least {GridFsOrphanSweepOptions.MinimumGracePeriod.TotalMinutes} minutes.");
        }

        var tenantContext = await _systemContext.TryFindTenantContextAsync(tenantId).ConfigureAwait(false);
        if (tenantContext == null)
        {
            return null;
        }

        // One run per tenant at a time on this instance: the sweep is idempotent, but two full scans of a large
        // tenant at once only cost I/O.
        var gate = _gates.GetOrAdd(tenantId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new GridFsOrphanSweepBusyException(tenantId);
        }

        try
        {
            var result = await _executor.SweepAsync(tenantContext.DatabaseName,
                new GridFsOrphanSweepOptions { Remove = remove, GracePeriod = gracePeriod }, cancellationToken)
                .ConfigureAwait(false);
            return new GridFsOrphanSweepReportDto
            {
                TenantId = tenantId,
                Mode = result.Removed ? "remove" : "dry-run",
                GracePeriodMinutes = result.GracePeriod.TotalMinutes,
                ExaminedFiles = result.ExaminedFiles,
                ReferencedFiles = result.ReferencedFiles,
                YoungFiles = result.YoungFiles,
                OrphanFiles = result.OrphanFiles,
                OrphanChunks = result.OrphanChunks,
                RemovedFiles = result.RemovedFiles,
                OrphanBytes = result.OrphanBytes,
                DurationMilliseconds = (long)result.Duration.TotalMilliseconds,
                FinishedUtc = DateTime.UtcNow
            };
        }
        finally
        {
            gate.Release();
        }
    }
}

/// <summary>
///     Registration of the GridFS orphan sweep (AB#6541).
/// </summary>
public static class GridFsOrphanSweepServiceCollectionExtensions
{
    /// <summary>
    ///     Registers the orphan sweep service and its engine-backed executor.
    /// </summary>
    public static IServiceCollection AddGridFsOrphanSweep(this IServiceCollection services)
    {
        services.AddSingleton<IGridFsOrphanSweepExecutor, GridFsOrphanSweepExecutor>();
        services.AddSingleton<IGridFsOrphanSweepService, GridFsOrphanSweepService>();
        return services;
    }
}
