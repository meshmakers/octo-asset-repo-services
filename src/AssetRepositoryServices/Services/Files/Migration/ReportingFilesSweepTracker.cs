using System.Collections.Concurrent;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files.Migration;

/// <summary>
///     Tenants on the straggler timer (AB#6175, Q3) with the time of their last finding (legacy data found,
///     sweep failed or skipped). A tenant stays on the timer for <see cref="FilesMigrationOptions.StragglerWindow" />
///     after its last finding — old writers can still appear during the rollout window even after a zero
///     check — or until System.Reporting 3.0.0+ is installed. Per instance and in memory: after a restart every
///     tenant is checked at its start anyway.
/// </summary>
public class ReportingFilesSweepTracker
{
    private readonly ConcurrentDictionary<string, DateTime> _lastFinding = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ReportingFilesPreCheckInfo> _preChecks = new(StringComparer.Ordinal);

    /// <summary>
    ///     Marks the background pre-check scan of the tenant as started (AB#6554). False when a scan of this
    ///     tenant is already running on this instance — a repeated tenant start must not start a second scan.
    /// </summary>
    public bool TryBeginPreCheck(string tenantId)
    {
        var started = new ReportingFilesPreCheckInfo(FilesMigrationPreCheckStates.Scanning, DateTime.UtcNow, null);
        while (true)
        {
            if (_preChecks.TryGetValue(tenantId, out var current))
            {
                if (current.State == FilesMigrationPreCheckStates.Scanning)
                {
                    return false;
                }

                if (_preChecks.TryUpdate(tenantId, started, current))
                {
                    return true;
                }
            }
            else if (_preChecks.TryAdd(tenantId, started))
            {
                return true;
            }
        }
    }

    /// <summary>
    ///     Records the end of the background pre-check scan of the tenant.
    /// </summary>
    public void CompletePreCheck(string tenantId, bool succeeded)
    {
        var startedUtc = _preChecks.TryGetValue(tenantId, out var current) ? current.StartedUtc : DateTime.UtcNow;
        _preChecks[tenantId] = new ReportingFilesPreCheckInfo(
            succeeded ? FilesMigrationPreCheckStates.Scanned : FilesMigrationPreCheckStates.Failed, startedUtc,
            DateTime.UtcNow);
    }

    /// <summary>
    ///     State of the background pre-check scan of the tenant on this instance; null when none ran since the start.
    /// </summary>
    public ReportingFilesPreCheckInfo? GetPreCheck(string tenantId)
    {
        return _preChecks.TryGetValue(tenantId, out var info) ? info : null;
    }

    /// <summary>
    ///     Records a finding now: the tenant is (again) on the timer for the whole window.
    /// </summary>
    public void MarkPending(string tenantId)
    {
        _lastFinding[tenantId] = DateTime.UtcNow;
    }

    /// <summary>
    ///     Takes the tenant off the timer.
    /// </summary>
    public void Clear(string tenantId)
    {
        _lastFinding.TryRemove(tenantId, out _);
    }

    /// <summary>
    ///     True when the tenant is on the timer.
    /// </summary>
    public bool IsPending(string tenantId)
    {
        return _lastFinding.ContainsKey(tenantId);
    }

    /// <summary>
    ///     Takes the tenant off the timer when its last finding is older than the window; true when it stays.
    /// </summary>
    public bool ExpireIfQuiet(string tenantId, TimeSpan window)
    {
        if (!_lastFinding.TryGetValue(tenantId, out var lastFinding))
        {
            return false;
        }

        if (DateTime.UtcNow - lastFinding < window)
        {
            return true;
        }

        _lastFinding.TryRemove(new KeyValuePair<string, DateTime>(tenantId, lastFinding));
        return false;
    }

    /// <summary>
    ///     Test hook: pretends the last finding happened at the given time.
    /// </summary>
    internal void SetLastFinding(string tenantId, DateTime lastFindingUtc)
    {
        _lastFinding[tenantId] = lastFindingUtc;
    }

    /// <summary>
    ///     Snapshot of the tenants on the timer.
    /// </summary>
    public IReadOnlyList<string> GetPendingTenants()
    {
        return _lastFinding.Keys.ToList();
    }
}

/// <summary>
///     State of the background pre-check scan of one tenant (AB#6554).
/// </summary>
/// <param name="State">One of <see cref="FilesMigrationPreCheckStates" />.</param>
/// <param name="StartedUtc">Start of the scan.</param>
/// <param name="FinishedUtc">End of the scan; null while it runs.</param>
public sealed record ReportingFilesPreCheckInfo(string State, DateTime StartedUtc, DateTime? FinishedUtc);
