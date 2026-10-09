using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files.Migration;

/// <summary>
///     Runs <see cref="ReportingFilesMoveSweep" /> for the two triggers of AB#6175 (Q3) and keeps the
///     straggler tracker up to date. Never throws: a sweep problem must not fail a tenant start.
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

    private readonly ReportingFilesMoveSweep _sweep;
    private readonly FilesMigrationStatusService _statusService;
    private readonly ReportingFilesSweepTracker _tracker;
    private readonly IOptionsMonitor<FilesMigrationOptions> _options;
    private readonly ILogger<ReportingFilesSweepRunner> _logger;

    /// <summary>
    ///     Constructor.
    /// </summary>
    public ReportingFilesSweepRunner(ReportingFilesMoveSweep sweep, FilesMigrationStatusService statusService,
        ReportingFilesSweepTracker tracker, IOptionsMonitor<FilesMigrationOptions> options,
        ILogger<ReportingFilesSweepRunner> logger)
    {
        _sweep = sweep;
        _statusService = statusService;
        _tracker = tracker;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    ///     Tenant start (after the System.Files import and the service migrations): one log line with the
    ///     cheap check; when legacy data is found, the full pre-check report is logged first (counts,
    ///     orphans, literal references), then the data is moved.
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
    ///     Straggler timer: sweeps the tenants whose last sweep found legacy data. A tenant whose check
    ///     comes back zero leaves the timer until its next start.
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
        if (logPreCheck)
        {
            // The pre-check scans every entity collection, so it only runs when there is something to
            // move — the moment its list matters (pipelines and policies that still name the old types).
            var status = await _statusService.GetStatusAsync(tenantId, cancellationToken).ConfigureAwait(false);
            if (status != null && !status.Legacy.IsZero)
            {
                LogPreCheck(status);
            }
        }

        var result = await _sweep.SweepAsync(tenantId, trigger, cancellationToken).ConfigureAwait(false);
        switch (result.Outcome)
        {
            case ReportingFilesSweepOutcome.NothingToDo:
            case ReportingFilesSweepOutcome.TenantNotFound:
                _tracker.Clear(tenantId);
                break;
            case ReportingFilesSweepOutcome.Disabled:
                break;
            default:
                // Moved (stragglers may follow while old writers are still around), mismatch, failure or
                // missing target: keep checking every interval until a check comes back zero.
                _tracker.MarkPending(tenantId);
                break;
        }

        return result;
    }

    private void LogPreCheck(FilesMigrationStatusDto status)
    {
        _logger.LogInformation(
            "{Sweep}: pre-check tenant '{TenantId}': legacy {Legacy}; orphans without parent: {Orphans}; " +
            "entities naming System.Reporting file types outside the file collections: {References} " +
            "(scanned {Documents} documents in {Collections} collections)",
            ReportingFilesMigrationConstants.SweepName, status.TenantId, status.Legacy, status.OrphanCount,
            status.LiteralReferenceCount, status.ScannedDocuments, status.ScannedCollections);

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
