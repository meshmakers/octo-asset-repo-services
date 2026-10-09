using System.Collections.Concurrent;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files.Migration;

/// <summary>
///     Tenants whose last sweep found legacy data (or failed) and therefore stay on the straggler timer
///     (AB#6175, Q3). Per instance and in memory: after a restart every tenant is checked at its start anyway.
/// </summary>
public class ReportingFilesSweepTracker
{
    private readonly ConcurrentDictionary<string, DateTime> _pending = new(StringComparer.Ordinal);

    /// <summary>
    ///     Keeps the tenant on the straggler timer.
    /// </summary>
    public void MarkPending(string tenantId)
    {
        _pending[tenantId] = DateTime.UtcNow;
    }

    /// <summary>
    ///     Takes the tenant off the straggler timer.
    /// </summary>
    public void Clear(string tenantId)
    {
        _pending.TryRemove(tenantId, out _);
    }

    /// <summary>
    ///     True when the tenant is on the straggler timer.
    /// </summary>
    public bool IsPending(string tenantId)
    {
        return _pending.ContainsKey(tenantId);
    }

    /// <summary>
    ///     Snapshot of the tenants on the straggler timer.
    /// </summary>
    public IReadOnlyList<string> GetPendingTenants()
    {
        return _pending.Keys.ToList();
    }
}
