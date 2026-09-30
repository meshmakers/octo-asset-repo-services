using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Observability;

/// <summary>
///     Periodic sweep that publishes CK model health as OpenTelemetry metrics for every tenant that
///     opted in (AB#5432).
///
///     <para>
///         This replaces a blueprint pipeline that pushed OTLP itself. That pipeline existed because
///         no self-emitted service metric could reach the backend before AB#5430, and it paid for
///         that with client mirroring and a per-tenant <c>acr_values</c> dance, because AB#5032/5077
///         closed cross-tenant client-credentials tokens. None of that is needed here: this service
///         holds every tenant's <c>LibraryStatus</c> first-hand, so there is no token to mint and no
///         tenant boundary to cross.
///     </para>
///     <para>
///         <b>Failure isolation is the whole design.</b> One unreachable tenant must not stop the
///         sweep (the other tenants would silently stop being measured), must not fail the sweep
///         (the heartbeat would then report the sweep as dead when only one tenant is), and must not
///         hang the sweep (a stuck catalog read would freeze every other tenant's data at its last
///         value). So: per-tenant timeout, per-tenant try/catch, and the tenant's own
///         <c>octo.ck.tenant.unreachable</c> series instead of a log line nobody reads.
///     </para>
/// </summary>
internal sealed class CkModelObservabilitySweepService : BackgroundService
{
    private readonly ILogger<CkModelObservabilitySweepService> _logger;
    private readonly IOptionsMonitor<CkModelObservabilityOptions> _options;
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>
    ///     Faults reported on the previous sweep, per tenant, so each sweep can log the delta
    ///     instead of the state. The opt-in is checked on every sweep and must not produce a log
    ///     line per tenant per interval — that is what made the predecessor's logs unreadable, and
    ///     an unreadable log is why nobody noticed it had stopped.
    /// </summary>
    private readonly Dictionary<string, HashSet<string>> _reportedFaults = new(StringComparer.Ordinal);

    private readonly HashSet<string> _unreachableTenants = new(StringComparer.Ordinal);

    public CkModelObservabilitySweepService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<CkModelObservabilityOptions> options,
        ILogger<CkModelObservabilitySweepService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.CurrentValue;
        if (!options.IsEnabled)
        {
            // Deliberately does NOT arm the instruments: a deployment that switched the sweep off
            // must produce no series at all, not a dead man's switch that fires forever.
            _logger.LogInformation(
                "CK model observability sweep is disabled ({Section}:IsEnabled=false); no CK model health metrics will be published.",
                CkModelObservabilityOptions.SectionName);
            return;
        }

        CkModelObservabilityMetrics.Arm(options.StalenessWindow);

        _logger.LogInformation(
            "CK model observability sweep starting. Startup delay: {StartupDelay}, interval: {Interval}, staleness window: {StalenessWindow}, per-tenant timeout: {PerTenantTimeout}.",
            options.StartupDelay, options.Interval, options.StalenessWindow, options.PerTenantTimeout);

        try
        {
            await Task.Delay(options.StartupDelay, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await SweepAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // A top-level failure (tenant enumeration, for instance) must not kill the
                    // service. It also must not refresh the heartbeat — RecordSweepCompleted is
                    // only reached at the end of a sweep that finished, so a service stuck in this
                    // branch ages out and octo.ck.health.sweep.age fires.
                    _logger.LogError(ex, "CK model health sweep failed; retrying on the next interval.");
                }

                try
                {
                    await Task.Delay(_options.CurrentValue.Interval, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown requested.
        }

        _logger.LogInformation("CK model observability sweep stopped.");
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;

        using var scope = _scopeFactory.CreateScope();
        var systemContext = scope.ServiceProvider.GetRequiredService<ISystemContext>();
        var optIn = scope.ServiceProvider.GetRequiredService<ITenantObservabilityOptIn>();
        var libraryStatusService = scope.ServiceProvider.GetRequiredService<ICkModelLibraryStatusService>();

        var tenantIds = await GetTenantIdsAsync(systemContext);
        var observed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var tenantId in tenantIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool isOptedIn;
            try
            {
                isOptedIn = await optIn.IsCkModelObservabilityEnabledAsync(tenantId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The opt-in itself was unreadable. This is NOT reported as an unreachable tenant:
                // we do not know that the tenant wanted to be measured, and a tenant without the
                // System model imported would otherwise light up the CkHealthTenantUnreachable rule
                // forever for a condition nobody asked to be told about. Debug, not warning — this
                // runs for every tenant on every interval.
                _logger.LogDebug(ex,
                    "Could not read the CK model observability opt-in of tenant '{TenantId}'; treating it as not opted in.",
                    tenantId);
                CkModelObservabilityMetrics.Forget(tenantId);
                continue;
            }

            if (!isOptedIn)
            {
                // Silent by contract. Forget() matters: a tenant that opts back out must stop being
                // exported instead of keeping its last faults on the wire until the pod restarts.
                CkModelObservabilityMetrics.Forget(tenantId);
                _reportedFaults.Remove(tenantId);
                _unreachableTenants.Remove(tenantId);
                continue;
            }

            observed.Add(tenantId);
            await SweepTenantAsync(libraryStatusService, tenantId, options.PerTenantTimeout, cancellationToken);
        }

        // Tenants that vanished from the registry (deleted, or moved out of this host's scope) must
        // not keep their last snapshot on the wire.
        foreach (var staleTenantId in _reportedFaults.Keys.Where(t => !observed.Contains(t)).ToList())
        {
            CkModelObservabilityMetrics.Forget(staleTenantId);
            _reportedFaults.Remove(staleTenantId);
            _unreachableTenants.Remove(staleTenantId);
        }

        CkModelObservabilityMetrics.RecordSweepCompleted();

        _logger.LogDebug("CK model health sweep completed. Tenants enumerated: {Total}, opted in: {Observed}.",
            tenantIds.Count, observed.Count);
    }

    private async Task SweepTenantAsync(ICkModelLibraryStatusService libraryStatusService, string tenantId,
        TimeSpan perTenantTimeout, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(perTenantTimeout);

            var status = await libraryStatusService.GetLibraryStatusAsync(tenantId, timeout.Token);
            var faults = CkModelHealthEvaluator.Evaluate(status);

            CkModelObservabilityMetrics.SetTenantFaults(tenantId, faults);
            LogFaultDelta(tenantId, faults);

            if (_unreachableTenants.Remove(tenantId))
            {
                _logger.LogInformation("CK model health of tenant '{TenantId}' is readable again.", tenantId);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            CkModelObservabilityMetrics.SetTenantUnreachable(tenantId);
            _reportedFaults.Remove(tenantId);

            // Warn once per transition, not once per interval.
            if (_unreachableTenants.Add(tenantId))
            {
                _logger.LogWarning(ex,
                    "Could not read the CK model library status of tenant '{TenantId}'; publishing {Metric}=1.",
                    tenantId, CkModelObservabilityMetrics.TenantUnreachableMetricName);
            }
        }
    }

    /// <summary>
    ///     Logs only what changed since the previous sweep. The metric carries the state; the log
    ///     carries the transition and the detail that is deliberately not a label (which dependency,
    ///     which catalog, what the incompatibility reason said).
    /// </summary>
    private void LogFaultDelta(string tenantId, IReadOnlyList<CkLibraryFault> faults)
    {
        var current = new HashSet<string>(
            faults.Select(f => $"{f.Library}-{f.Version}={f.State}"), StringComparer.Ordinal);

        if (!_reportedFaults.TryGetValue(tenantId, out var previous))
        {
            previous = new HashSet<string>(StringComparer.Ordinal);
        }

        foreach (var added in current.Where(c => !previous.Contains(c)))
        {
            _logger.LogWarning(
                "CK model health of tenant '{TenantId}': {Fault} (1 = catalog inconsistency, 2 = ResolveFailed).",
                tenantId, added);
        }

        foreach (var cleared in previous.Where(p => !current.Contains(p)))
        {
            _logger.LogInformation("CK model health of tenant '{TenantId}': {Fault} is no longer reported.",
                tenantId, cleared);
        }

        _reportedFaults[tenantId] = current;
    }

    private static async Task<IReadOnlyList<string>> GetTenantIdsAsync(ISystemContext systemContext)
    {
        using var adminSession = await systemContext.GetAdminSessionAsync();
        var resultSet = await systemContext.GetAllTenantsAsync(adminSession);
        return resultSet.Items.Select(t => t.TenantId).ToList();
    }
}
