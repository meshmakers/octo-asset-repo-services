using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Microsoft.AspNetCore.Http;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files;

/// <summary>
///     AB#6477: serialises the "check that the sibling name is free, then insert" window of the platform file
///     system. A lock is keyed by parent rtId and case-insensitive name, belongs to an <see cref="IOctoSession" />
///     and is held until <see cref="Release" /> — i.e. until the session's transaction is committed or aborted —
///     so a second writer re-reads the committed state after the first one is visible. Locks are per process; the
///     asset repository runs the file system writes of one tenant in the replica that serves the request, a
///     multi-replica deployment narrows the window but does not close it (documented in CLAUDE.md).
/// </summary>
public sealed class FileSystemNameLocks
{
    /// <summary>
    ///     Longest wait for a name lock before the request is answered with a retryable 409.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<IOctoSession, HashSet<string>> _held = new();

    /// <summary>
    ///     Lock key of a sibling name below a parent.
    /// </summary>
    public static string KeyOf(OctoObjectId parentRtId, string name) =>
        $"{parentRtId}/{name.ToUpperInvariant()}";

    /// <summary>
    ///     Number of locks currently held or awaited (diagnostics, tests).
    /// </summary>
    public int ActiveCount
    {
        get
        {
            lock (_gate)
            {
                return _slots.Count;
            }
        }
    }

    /// <summary>
    ///     Acquires the keys for <paramref name="session" /> in a stable order (no deadlock between callers of one
    ///     batch). Keys the session already holds are skipped. Throws a retryable 409 after
    ///     <paramref name="timeout" />.
    /// </summary>
    public async Task AcquireAsync(IOctoSession session, IEnumerable<string> keys, TimeSpan? timeout = null)
    {
        var held = _held.GetOrCreateValue(session);
        var wanted = keys.Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var acquired = new List<string>();
        try
        {
            foreach (var key in wanted)
            {
                lock (held)
                {
                    if (held.Contains(key))
                    {
                        continue;
                    }
                }

                var slot = Enter(key);
                bool taken;
                try
                {
                    taken = await slot.Semaphore.WaitAsync(timeout ?? DefaultTimeout).ConfigureAwait(false);
                }
                catch
                {
                    Leave(key);
                    throw;
                }

                if (!taken)
                {
                    Leave(key);
                    throw new FileSystemException(FileSystemErrorCodes.InvalidRequest, StatusCodes.Status409Conflict,
                        "Another change of the same folder is still running; retry.");
                }

                lock (held)
                {
                    held.Add(key);
                }

                acquired.Add(key);
            }
        }
        catch
        {
            foreach (var key in acquired)
            {
                lock (held)
                {
                    held.Remove(key);
                }

                Exit(key);
            }

            throw;
        }
    }

    /// <summary>
    ///     Releases every lock <paramref name="session" /> holds. Idempotent; call it after commit or abort.
    /// </summary>
    public void Release(IOctoSession session)
    {
        if (!_held.TryGetValue(session, out var held))
        {
            return;
        }

        string[] keys;
        lock (held)
        {
            keys = held.ToArray();
            held.Clear();
        }

        foreach (var key in keys)
        {
            Exit(key);
        }
    }

    private Slot Enter(string key)
    {
        lock (_gate)
        {
            if (!_slots.TryGetValue(key, out var slot))
            {
                slot = new Slot();
                _slots[key] = slot;
            }

            slot.Users++;
            return slot;
        }
    }

    private void Leave(string key)
    {
        lock (_gate)
        {
            if (_slots.TryGetValue(key, out var slot) && --slot.Users == 0)
            {
                _slots.Remove(key);
            }
        }
    }

    private void Exit(string key)
    {
        Slot? slot;
        lock (_gate)
        {
            _slots.TryGetValue(key, out slot);
        }

        slot?.Semaphore.Release();
        Leave(key);
    }

    private sealed class Slot
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int Users { get; set; }
    }
}
