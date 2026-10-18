using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files.Migration;

/// <summary>
///     Runs <see cref="ReportingFilesMoveSweep" /> for the two triggers of AB#6175 (Q3) and keeps the
///     straggler tracker up to date. Never throws: a sweep problem must not fail a tenant start.
///     <para>
///         Tenant start runs on every pod for every tenant (and again on every broadcast tenant event), so the
///         start path first runs only the cheap check; the pre-check scan and the move happen only when legacy
///         data exists, and only on the pod that holds the tenant's sweep lease.
///     </para>
/// </summary>
public class ReportingFilesSweepRunner : IDisposable
{
    /// <summary>
    ///     Trigger name of the run at tenant start.
    /// </summary>
    public const string TenantStartTrigger = "TenantStart";

    /// <summary>
    ///     Trigger name of the straggler timer.
    /// </summary>
    public const string StragglerTimerTrigger = "StragglerTimer";

    private const int MaxLoggedReferences = 50;

    private readonly ITenantMongoDatabaseProvider _databaseProvider;
    private readonly ReportingFilesMoveSweep _sweep;
    private readonly FilesMigrationStatusService _statusService;
    private readonly ReportingFilesSweepTracker _tracker;
    private readonly IOptionsMonitor<FilesMigrationOptions> _options;
    private readonly ILogger<ReportingFilesSweepRunner> _logger;
    private readonly ConcurrentDictionary<(string TenantId, ReportingFilesSweepOutcome Outcome), DateTime> _lastWarnings = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<string, Task> _preCheckTasks = new(StringComparer.Ordinal);
    private int _unknownTenantsChecked;

    /// <summary>
    ///     Constructor.
    /// </summary>
    public ReportingFilesSweepRunner(ITenantMongoDatabaseProvider databaseProvider, ReportingFilesMoveSweep sweep,
        FilesMigrationStatusService statusService, ReportingFilesSweepTracker tracker,
        IOptionsMonitor<FilesMigrationOptions> options, ILogger<ReportingFilesSweepRunner> logger)
    {
        _databaseProvider = databaseProvider;
        _sweep = sweep;
        _statusService = statusService;
        _tracker = tracker;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    ///     Tenant start (after the System.Files import and the default root): one log line with the cheap
    ///     check; when legacy data is found, the lease holder logs the full pre-check report (counts, orphans,
    ///     literal references) and moves the data. Only for a tenant the sweep is allowed for (global switch on
    ///     and tenant listed, AB#6473); every other tenant gets one skip line and is not touched.
    /// </summary>
    public async Task<ReportingFilesSweepResult?> RunAtTenantStartAsync(string tenantId,
        CancellationToken cancellationToken = default)
    {
        var options = _options.CurrentValue;
        if (options.SweepEnabled)
        {
            await WarnAboutUnknownSweepTenantsOnceAsync(options).ConfigureAwait(false);
        }

        if (!options.IsSweepAllowed(tenantId))
        {
            return LogSkipped(tenantId, options);
        }

        try
        {
            var result = await RunCoreAsync(tenantId, TenantStartTrigger, logPreCheck: true, cancellationToken)
                .ConfigureAwait(false);
            if (result.Outcome == ReportingFilesSweepOutcome.NothingToDo)
            {
                _logger.LogInformation(
                    "{Sweep}: tenant '{TenantId}' has no System.Reporting file data; nothing to do",
                    ReportingFilesMigrationConstants.SweepName, tenantId);
            }

            return result;
        }
        catch (Exception ex)
        {
            _tracker.MarkPending(tenantId);
            _logger.LogError(ex, "{Sweep}: tenant '{TenantId}' could not be swept at tenant start; retrying on the timer",
                ReportingFilesMigrationConstants.SweepName, tenantId);
            return null;
        }
    }

    /// <summary>
    ///     Straggler timer: sweeps the tenants on the timer. A tenant leaves the timer once its last finding is
    ///     older than <see cref="FilesMigrationOptions.StragglerWindow" /> or System.Reporting 3.0.0+ is installed.
    /// </summary>
    public async Task RunStragglerSweepAsync(CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.SweepEnabled)
        {
            return;
        }

        foreach (var tenantId in _tracker.GetPendingTenants())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_options.CurrentValue.IsSweepAllowed(tenantId))
            {
                // Taken off the allow-list while on the timer: stop touching it.
                _tracker.Clear(tenantId);
                continue;
            }

            try
            {
                await RunCoreAsync(tenantId, StragglerTimerTrigger, logPreCheck: false, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Sweep}: straggler sweep of tenant '{TenantId}' failed; retrying on the next interval",
                    ReportingFilesMigrationConstants.SweepName, tenantId);
            }
        }
    }

    /// <summary>
    ///     One line per tenant start for a tenant the sweep does not run for (AB#6473) and NO database access: the
    ///     tenant start is awaited by the tenant setup, and a legacy count (an unindexed regex count on fs.files)
    ///     per tenant per pod on the default path would be a regression (review of the PR). With the switch off
    ///     the line is informational (as before); with the switch on and the tenant not listed it is a
    ///     <b>warning</b> — the level asset-repo logs on staging and prod (nlog.config defaults Meshmakers.* to Warn,
    ///     an info line is invisible there) and exactly the window in which the operator needs to see which tenants
    ///     are skipped. Whether the tenant holds legacy data is not looked up here; the migration status answers that
    ///     on demand (<c>GET system/v1/files/migration-status/{tenantId}</c>, field <c>sweep</c>).
    /// </summary>
    private ReportingFilesSweepResult LogSkipped(string tenantId, FilesMigrationOptions options)
    {
        if (!options.SweepEnabled)
        {
            _logger.LogInformation("{Sweep}: disabled ({Section}:SweepEnabled=false); tenant '{TenantId}' not checked",
                ReportingFilesMigrationConstants.SweepName, FilesMigrationOptions.SectionName, tenantId);
            return new ReportingFilesSweepResult { TenantId = tenantId, Outcome = ReportingFilesSweepOutcome.Disabled };
        }

        _logger.LogWarning(
            "{Sweep}: tenant '{TenantId}' is NOT swept and not checked for legacy System.Reporting file data: it is not in " +
            "{Section}:SweepTenants (see GET system/v1/files/migration-status/{TenantId} for its state)",
            ReportingFilesMigrationConstants.SweepName, tenantId, FilesMigrationOptions.SectionName, tenantId);
        return new ReportingFilesSweepResult { TenantId = tenantId, Outcome = ReportingFilesSweepOutcome.NotListed };
    }

    /// <summary>
    ///     Once per process, at the first tenant start while the switch is on (a system-tenant lookup per listed id, no data access): warns about
    ///     listed tenant ids that match no tenant right now (typos, or a tenant that does not exist yet — a tenant
    ///     created later is still swept at its start). Never runs while the switch is off.
    /// </summary>
    private async Task WarnAboutUnknownSweepTenantsOnceAsync(FilesMigrationOptions options)
    {
        if (Interlocked.Exchange(ref _unknownTenantsChecked, 1) == 1)
        {
            return;
        }

        foreach (var listed in options.SweepTenants.Where(t => !string.IsNullOrWhiteSpace(t))
                     .Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                // The list matches case-insensitively, the tenant lookup does not: try the id as written and lower-case.
                var known = await _databaseProvider.TryGetDatabaseAsync(listed).ConfigureAwait(false) ??
                            await _databaseProvider.TryGetDatabaseAsync(listed.ToLowerInvariant()).ConfigureAwait(false);
                if (known == null)
                {
                    _logger.LogWarning(
                        "{Sweep}: {Section}:SweepTenants lists '{TenantId}', which is not (yet) a tenant of this installation; " +
                        "it is swept once a tenant with that id starts",
                        ReportingFilesMigrationConstants.SweepName, FilesMigrationOptions.SectionName, listed);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "{Sweep}: could not check whether '{TenantId}' of {Section}:SweepTenants exists",
                    ReportingFilesMigrationConstants.SweepName, listed, FilesMigrationOptions.SectionName);
            }
        }
    }

    private async Task<ReportingFilesSweepResult> RunCoreAsync(string tenantId, string trigger, bool logPreCheck,
        CancellationToken cancellationToken)
    {
        var database = await _databaseProvider.TryGetDatabaseAsync(tenantId).ConfigureAwait(false);
        if (database == null)
        {
            _tracker.Clear(tenantId);
            return new ReportingFilesSweepResult { TenantId = tenantId, Outcome = ReportingFilesSweepOutcome.TenantNotFound };
        }

        // Cheap check first: the scan and the lease only for tenants that have legacy data.
        var counts = await ReportingFilesMoveSweep.CountLegacyAsync(database, cancellationToken).ConfigureAwait(false);
        if (counts.IsZero)
        {
            await OnQuietAsync(tenantId, database, cancellationToken).ConfigureAwait(false);
            return new ReportingFilesSweepResult
            {
                TenantId = tenantId, Outcome = ReportingFilesSweepOutcome.NothingToDo, Before = counts
            };
        }

        ReportingFilesSweepResult result;
        // Without System.Files the sweep stops read-only (TargetModelMissing) — no lease, no scan.
        if (logPreCheck && await ReportingFilesMoveSweep.IsTargetReadyAsync(database, cancellationToken)
                .ConfigureAwait(false))
        {
            await using var lease = await TenantSweepLease.TryAcquireAsync(database, ReportingFilesMoveSweep.LeaseName,
                _options.CurrentValue.LeaseDuration, cancellationToken).ConfigureAwait(false);
            if (lease == null)
            {
                _logger.LogDebug("{Sweep}: tenant '{TenantId}' is swept by another instance; skipping",
                    ReportingFilesMigrationConstants.SweepName, tenantId);
                result = new ReportingFilesSweepResult
                {
                    TenantId = tenantId, Outcome = ReportingFilesSweepOutcome.LeaseHeld, Before = counts
                };
            }
            else
            {
                result = await _sweep.SweepAsync(tenantId, trigger, cancellationToken, lease).ConfigureAwait(false);

                // The pre-check scans every entity collection (3 min 8 s on a 1.3 million document tenant, AB#6554).
                // It only feeds the log report — the sweep finds root conflicts itself and fails closed — so it runs
                // in the background after the move and must not hold the tenant setup. Lease holder only; what it
                // reports is whatever legacy data is left after the move.
                if (result.Outcome != ReportingFilesSweepOutcome.NothingToDo)
                {
                    StartPreCheckInBackground(tenantId);
                }
            }
        }
        else
        {
            result = await _sweep.SweepAsync(tenantId, trigger, cancellationToken).ConfigureAwait(false);
        }

        switch (result.Outcome)
        {
            case ReportingFilesSweepOutcome.NothingToDo:
                await OnQuietAsync(tenantId, database, cancellationToken).ConfigureAwait(false);
                break;
            case ReportingFilesSweepOutcome.TenantNotFound:
                _tracker.Clear(tenantId);
                break;
            case ReportingFilesSweepOutcome.Disabled:
                break;
            case ReportingFilesSweepOutcome.TargetModelMissing:
                _tracker.MarkPending(tenantId);
                LogRepeated(tenantId, result.Outcome, LogLevel.Warning,
                    "tenant '{TenantId}' has legacy System.Reporting file data ({Before}) but System.Files is not imported; " +
                    "skipping the move", result.Before);
                break;
            case ReportingFilesSweepOutcome.RootConflict:
                _tracker.MarkPending(tenantId);
                LogRepeated(tenantId, result.Outcome, LogLevel.Error,
                    "tenant '{TenantId}' not moved: legacy folder roots collide with existing System.Files roots: {Conflicts}",
                    string.Join("; ", result.RootConflicts));
                break;
            default:
                // Moved (stragglers may follow while old writers are still around), lease held elsewhere,
                // mismatch or failure: keep checking every interval for the straggler window.
                _tracker.MarkPending(tenantId);
                break;
        }

        return result;
    }

    /// <summary>
    ///     Starts the read-only pre-check scan without awaiting it (AB#6554); one scan per tenant at a time.
    ///     It runs on its own token (not the tenant start's, which ends with the setup) that is cancelled when
    ///     the runner is disposed at shutdown. Never throws.
    /// </summary>
    private void StartPreCheckInBackground(string tenantId)
    {
        if (!_tracker.TryBeginPreCheck(tenantId))
        {
            _logger.LogDebug("{Sweep}: pre-check scan of tenant '{TenantId}' is already running",
                ReportingFilesMigrationConstants.SweepName, tenantId);
            return;
        }

        _logger.LogInformation("{Sweep}: pre-check scan of tenant '{TenantId}' started in the background",
            ReportingFilesMigrationConstants.SweepName, tenantId);
        var token = _shutdown.Token;
        var task = Task.Run(async () =>
        {
            try
            {
                var status = await _statusService.GetStatusAsync(tenantId, token).ConfigureAwait(false);
                if (status != null && !status.Legacy.IsZero)
                {
                    LogPreCheck(status);
                }

                _tracker.CompletePreCheck(tenantId, true);
            }
            catch (Exception ex)
            {
                _tracker.CompletePreCheck(tenantId, false);
                if (ex is OperationCanceledException && token.IsCancellationRequested)
                {
                    _logger.LogInformation("{Sweep}: pre-check scan of tenant '{TenantId}' cancelled at shutdown",
                        ReportingFilesMigrationConstants.SweepName, tenantId);
                }
                else
                {
                    _logger.LogWarning(ex, "{Sweep}: pre-check scan of tenant '{TenantId}' failed; the move is not affected",
                        ReportingFilesMigrationConstants.SweepName, tenantId);
                }
            }
        }, CancellationToken.None);
        _preCheckTasks[tenantId] = task;
        _ = task.ContinueWith(t => _preCheckTasks.TryRemove(new KeyValuePair<string, Task>(tenantId, t)),
            TaskScheduler.Default);
    }

    /// <summary>
    ///     Test hook: waits until the background pre-check scans started so far have finished.
    /// </summary>
    internal Task WaitForPreChecksAsync()
    {
        return Task.WhenAll(_preCheckTasks.Values.ToArray());
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task OnQuietAsync(string tenantId, MongoDB.Driver.IMongoDatabase database,
        CancellationToken cancellationToken)
    {
        if (!_tracker.IsPending(tenantId))
        {
            return;
        }

        if (await ReportingFilesMoveSweep.IsReporting3InstalledAsync(database, cancellationToken).ConfigureAwait(false))
        {
            _tracker.Clear(tenantId);
            return;
        }

        _tracker.ExpireIfQuiet(tenantId, _options.CurrentValue.StragglerWindow);
    }

    private void LogRepeated(string tenantId, ReportingFilesSweepOutcome outcome, LogLevel level, string message,
        object? argument)
    {
        var now = DateTime.UtcNow;
        var key = (tenantId, outcome);
        var due = !_lastWarnings.TryGetValue(key, out var last) ||
                  now - last >= _options.CurrentValue.RepeatedWarningInterval;
        if (due)
        {
            _lastWarnings[key] = now;
        }

        // ReSharper disable once TemplateIsNotCompileTimeConstantProblem
#pragma warning disable CA2254
        _logger.Log(due ? level : LogLevel.Debug, "{Sweep}: " + message, ReportingFilesMigrationConstants.SweepName,
            tenantId, argument);
#pragma warning restore CA2254
    }

    private void LogPreCheck(FilesMigrationStatusDto status)
    {
        _logger.LogInformation(
            "{Sweep}: pre-check tenant '{TenantId}': legacy {Legacy}; orphans without parent: {Orphans}; other types in the " +
            "legacy collection: {OtherTypes}; root conflicts: {RootConflicts}; entities naming System.Reporting file types " +
            "outside the file collections: {References} (ignored notification events: {Ignored}; scanned {Documents} documents in {Collections} collections, complete: {Complete})",
            ReportingFilesMigrationConstants.SweepName, status.TenantId, status.Legacy, status.OrphanCount,
            status.OtherLegacyTypes.Count == 0
                ? "none"
                : string.Join(", ", status.OtherLegacyTypes.Select(kv => $"{kv.Key}={kv.Value}")),
            status.RootConflicts.Count == 0 ? "none" : string.Join("; ", status.RootConflicts),
            status.LiteralReferenceCount, status.IgnoredLogReferenceCount, status.ScannedDocuments, status.ScannedCollections,
            status.LiteralScanComplete);

        foreach (var reference in status.LiteralReferences.Take(MaxLoggedReferences))
        {
            _logger.LogWarning(
                "{Sweep}: tenant '{TenantId}' entity {CkTypeId}@{RtId} ({Collection}, blueprint: {Blueprint}) names {Literals} in {Paths}; " +
                "the sweep does not rewrite it",
                ReportingFilesMigrationConstants.SweepName, status.TenantId, reference.CkTypeId, reference.RtId,
                reference.CollectionName, reference.RtBlueprintSource ?? "<tenant-local>",
                string.Join(", ", reference.Literals), string.Join(", ", reference.FieldPaths));
        }

        if (status.LiteralReferenceCount > MaxLoggedReferences)
        {
            _logger.LogWarning(
                "{Sweep}: tenant '{TenantId}' {More} more entities name System.Reporting file types; see GET system/v1/files/migration-status/{TenantId}",
                ReportingFilesMigrationConstants.SweepName, status.TenantId,
                status.LiteralReferenceCount - MaxLoggedReferences, status.TenantId);
        }
    }
}
