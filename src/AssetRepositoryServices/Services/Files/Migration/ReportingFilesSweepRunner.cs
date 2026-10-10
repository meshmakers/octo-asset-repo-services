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
public class ReportingFilesSweepRunner
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
    ///     literal references) and moves the data.
    /// </summary>
    public async Task<ReportingFilesSweepResult?> RunAtTenantStartAsync(string tenantId,
        CancellationToken cancellationToken = default)
    {
        if (!_options.CurrentValue.SweepEnabled)
        {
            _logger.LogInformation("{Sweep}: disabled ({Section}:SweepEnabled=false); tenant '{TenantId}' not checked",
                ReportingFilesMigrationConstants.SweepName, FilesMigrationOptions.SectionName, tenantId);
            return null;
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
                // The pre-check scans every entity collection; it runs once per tenant (lease holder) and only
                // when there is something to move — the moment its list matters.
                var status = await _statusService.GetStatusAsync(tenantId, cancellationToken).ConfigureAwait(false);
                if (status != null && !status.Legacy.IsZero)
                {
                    LogPreCheck(status);
                }

                result = await _sweep.SweepAsync(tenantId, trigger, cancellationToken, lease).ConfigureAwait(false);
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
            "outside the file collections: {References} (scanned {Documents} documents in {Collections} collections, complete: {Complete})",
            ReportingFilesMigrationConstants.SweepName, status.TenantId, status.Legacy, status.OrphanCount,
            status.OtherLegacyTypes.Count == 0
                ? "none"
                : string.Join(", ", status.OtherLegacyTypes.Select(kv => $"{kv.Key}={kv.Value}")),
            status.RootConflicts.Count == 0 ? "none" : string.Join("; ", status.RootConflicts),
            status.LiteralReferenceCount, status.ScannedDocuments, status.ScannedCollections, status.LiteralScanComplete);

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
