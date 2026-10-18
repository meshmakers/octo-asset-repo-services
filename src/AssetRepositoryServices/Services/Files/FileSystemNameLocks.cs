using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Microsoft.AspNetCore.Http;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files;

/// <summary>
///     AB#6477: serialises the "check that the sibling name is free, then insert" window of the platform file
///     system. A lock is keyed by parent rtId and case-insensitive name, belongs to an <see cref="IOctoSession" />
///     and is held until <see cref="ReleaseAsync" /> — i.e. until the session's transaction is committed or aborted —
///     so a second writer re-reads the committed state after the first one is visible.
///     <para>
///         Two levels (AB#6885): the in-process semaphore serialises the requests of one replica; when an
///         <see cref="IFileSystemNameLeaseStore" /> and a tenant id are given, a MongoDB lease per key additionally
///         serialises the replicas. Without them (unit tests, no admin credentials) only the in-process level applies.
///     </para>
/// </summary>
public sealed class FileSystemNameLocks(IFileSystemNameLeaseStore? leases = null)
{
    /// <summary>
    ///     Longest wait for a name lock before the request is answered with a retryable 409.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<IOctoSession, Holds> _held = new();

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
    ///     <paramref name="timeout" /> (the whole call, in-process wait and lease wait together).
    ///     <paramref name="tenantId" /> enables the cross-replica lease (see the class remarks).
    /// </summary>
    public async Task AcquireAsync(IOctoSession session, IEnumerable<string> keys, TimeSpan? timeout = null,
        string? tenantId = null)
    {
        var holds = _held.GetOrCreateValue(session);
        var wanted = keys.Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        var useLeases = leases != null && !string.IsNullOrEmpty(tenantId);
        var acquired = new List<string>();
        try
        {
            foreach (var key in wanted)
            {
                lock (holds)
                {
                    if (holds.Keys.Contains(key))
                    {
                        continue;
                    }
                }

                var slot = Enter(key);
                bool taken;
                try
                {
                    taken = await slot.Semaphore.WaitAsync(Remaining(deadline)).ConfigureAwait(false);
                }
                catch
                {
                    Leave(key);
                    throw;
                }

                if (!taken)
                {
                    Leave(key);
                    throw Busy();
                }

                if (useLeases)
                {
                    bool leased;
                    try
                    {
                        leased = await AcquireLeaseAsync(holds, tenantId!, key, deadline).ConfigureAwait(false);
                    }
                    catch
                    {
                        Exit(key);
                        throw;
                    }

                    if (!leased)
                    {
                        Exit(key);
                        throw Busy();
                    }
                }

                lock (holds)
                {
                    holds.Keys.Add(key);
                }

                acquired.Add(key);
            }
        }
        catch
        {
            foreach (var key in acquired)
            {
                lock (holds)
                {
                    holds.Keys.Remove(key);
                }

                await ReleaseLeaseAsync(holds, key).ConfigureAwait(false);
                Exit(key);
            }

            throw;
        }
    }

    /// <summary>
    ///     Releases every lock <paramref name="session" /> holds, the cross-replica leases first. Idempotent; call
    ///     it after commit or abort.
    /// </summary>
    public async Task ReleaseAsync(IOctoSession session)
    {
        if (!_held.TryGetValue(session, out var holds))
        {
            return;
        }

        string[] keys;
        lock (holds)
        {
            keys = holds.Keys.ToArray();
            holds.Keys.Clear();
        }

        foreach (var key in keys)
        {
            await ReleaseLeaseAsync(holds, key).ConfigureAwait(false);
            Exit(key);
        }
    }

    private static TimeSpan Remaining(DateTime deadline)
    {
        var remaining = deadline - DateTime.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static FileSystemException Busy() =>
        new(FileSystemErrorCodes.InvalidRequest, StatusCodes.Status409Conflict,
            "Another change of the same folder is still running; retry.");

    private async Task<bool> AcquireLeaseAsync(Holds holds, string tenantId, string key, DateTime deadline)
    {
        var delay = 20;
        while (true)
        {
            using var cts = new CancellationTokenSource(Remaining(deadline));
            try
            {
                if (await leases!.TryAcquireAsync(tenantId, key, holds.Owner, cts.Token).ConfigureAwait(false))
                {
                    lock (holds)
                    {
                        holds.Leased[key] = tenantId;
                    }

                    return true;
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                return false;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            // Another replica holds the name: poll with a short, growing, jittered pause.
            await Task.Delay(
                    TimeSpan.FromMilliseconds(Math.Min(delay + Random.Shared.Next(0, 20),
                        Math.Max(1, Remaining(deadline).TotalMilliseconds))))
                .ConfigureAwait(false);
            delay = Math.Min(delay * 2, 250);
        }
    }

    private async Task ReleaseLeaseAsync(Holds holds, string key)
    {
        string? tenantId;
        lock (holds)
        {
            if (!holds.Leased.Remove(key, out tenantId))
            {
                return;
            }
        }

        await leases!.ReleaseAsync(tenantId, key, holds.Owner).ConfigureAwait(false);
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

    private sealed class Holds
    {
        /// <summary>
        ///     Keys the session holds in-process.
        /// </summary>
        public HashSet<string> Keys { get; } = new(StringComparer.Ordinal);

        /// <summary>
        ///     Keys with a cross-replica lease, and the tenant they were leased in.
        /// </summary>
        public Dictionary<string, string> Leased { get; } = new(StringComparer.Ordinal);

        /// <summary>
        ///     Lease owner of this session (a lease is only released by its owner).
        /// </summary>
        public string Owner { get; } = $"{Environment.MachineName}/{Guid.NewGuid():N}";
    }

    private sealed class Slot
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int Users { get; set; }
    }
}
