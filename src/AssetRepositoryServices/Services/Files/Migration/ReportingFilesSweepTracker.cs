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
