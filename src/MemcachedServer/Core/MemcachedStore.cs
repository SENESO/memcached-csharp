using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace MemcachedServer.Core;

public enum StorageKind
{
    Set,
    Add,
    Replace,
    Append,
    Prepend,
    Cas
}

public enum StorageOutcome
{
    Stored,
    NotStored,
    Exists,
    NotFound,
    TooLarge
}

public enum DeltaOutcome
{
    Ok,
    NotFound,
    NonNumeric
}

/// <summary>Result of a single-key lookup; <see cref="Value"/> is never mutated after being handed out.</summary>
public readonly record struct GetOutcome(bool Found, byte[] Value, uint Flags, ulong CasId)
{
    public static readonly GetOutcome Miss = new(false, Array.Empty<byte>(), 0, 0);
}

/// <summary>
/// The cache itself: a thread-safe, size-bounded, expiring key/value store
/// with LRU eviction and Memcached semantics.
///
/// Concurrency design:
/// <list type="bullet">
/// <item>Operations on the same key serialise on one of 64 lock stripes
/// (chosen by the key), so read-modify-write commands (<c>incr</c>,
/// <c>cas</c>, <c>append</c>…) are atomic per key without a global lock.</item>
/// <item>Every structural change — map membership, LRU order, the byte
/// ledger — happens under a single short-held gate, always acquired
/// <em>after</em> a stripe, which keeps lock ordering deadlock-free.</item>
/// <item>Entry fields are read and written only under the owning stripe.
/// Entry value arrays are never mutated in place; updates replace them,
/// so a value handed to a reader stays stable.</item>
/// <item>Eviction removes LRU victims without taking their stripe (a cache
/// may always drop an entry under a racing operation, exactly like
/// Memcached); an operation that loses its entry this way revalidates map
/// membership inside the gate before committing.</item>
/// </list>
/// Expiry is lazy (checked on every access) plus a periodic sweep driven by
/// <see cref="RunMaintenance"/>.
/// </summary>
public sealed class MemcachedStore
{
    private const int StripeCount = 64;

    private readonly ConcurrentDictionary<string, CacheEntry> _items = new(StringComparer.Ordinal);
    private readonly object[] _stripes;
    private readonly object _gate = new();

    // LRU order of keys: First = most recently used, Last = next victim.
    // Both structures and _currentBytes are guarded by _gate.
    private readonly LinkedList<string> _lru = new();
    private readonly Dictionary<string, LinkedListNode<string>> _lruNodes = new(StringComparer.Ordinal);
    private long _currentBytes;

    private long _casCounter;
    private DateTimeOffset? _flushDueAt; // guarded by _gate

    private readonly TimeProvider _clock;
    private readonly CacheStats _stats;
    private readonly long _maxMemoryBytes;
    private readonly int _maxValueBytes;

    public MemcachedStore(TimeProvider clock, CacheStats stats, long maxMemoryBytes, int maxValueBytes)
    {
        _clock = clock;
        _stats = stats;
        _maxMemoryBytes = maxMemoryBytes;
        _maxValueBytes = maxValueBytes;
        _stripes = new object[StripeCount];
        for (var i = 0; i < _stripes.Length; i++)
            _stripes[i] = new object();
    }

    public int Count => _items.Count;

    public long CurrentBytes
    {
        get { lock (_gate) return _currentBytes; }
    }

    public GetOutcome Get(string key)
    {
        ExecuteFlushIfDue();

        lock (StripeFor(key))
        {
            if (!_items.TryGetValue(key, out var entry))
                return GetOutcome.Miss;

            if (entry.IsExpired(_clock.GetUtcNow()))
            {
                lock (_gate)
                    RemoveLocked(key, entry);
                return GetOutcome.Miss;
            }

            var outcome = new GetOutcome(true, entry.Value, entry.Flags, entry.CasId);
            lock (_gate)
                TouchLruLocked(key);
            return outcome;
        }
    }

    public StorageOutcome Store(StorageKind kind, string key, uint flags, long exptime, byte[] data, ulong casId = 0)
    {
        if (data.Length > _maxValueBytes)
            return StorageOutcome.TooLarge;

        ExecuteFlushIfDue();

        lock (StripeFor(key))
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            var exists = TryGetLiveLocked(key, now, out var entry);

            switch (kind)
            {
                case StorageKind.Add when exists:
                case StorageKind.Replace when !exists:
                    return StorageOutcome.NotStored;

                case StorageKind.Append:
                case StorageKind.Prepend:
                    if (!exists)
                        return StorageOutcome.NotStored;
                    return AppendPrependLocked(kind, key, entry!, data);

                case StorageKind.Cas:
                    if (!exists)
                        return StorageOutcome.NotFound;
                    if (entry!.CasId != casId)
                        return StorageOutcome.Exists;
                    break;
            }

            // Set-like path: Set, Add (absent), Replace (present), Cas (matched).
            var newSize = key.Length + (long)data.Length;
            if (newSize > _maxMemoryBytes)
                return StorageOutcome.TooLarge;

            var oldSize = exists ? entry!.Size : 0;
            var isNew = !exists;

            if (isNew)
            {
                entry = new CacheEntry(key, data, flags, ExpiryCalculator.ToExpiresAt(exptime, _clock), NextCasId());
                _items[key] = entry;
            }
            else
            {
                entry!.Value = data;
                entry.Flags = flags;
                entry.ExpiresAt = ExpiryCalculator.ToExpiresAt(exptime, _clock);
                entry.CasId = NextCasId();
            }

            _currentBytes += newSize - oldSize;
            TouchLruLocked(key);
            EvictWhileOverLimitLocked(protectedKey: key);

            if (isNew)
                _stats.AddTotalItem();
            return StorageOutcome.Stored;
        }
    }

    public bool Delete(string key)
    {
        ExecuteFlushIfDue();

        lock (StripeFor(key))
        lock (_gate)
        {
            if (!TryGetLiveLocked(key, _clock.GetUtcNow(), out var entry))
                return false;
            RemoveLocked(key, entry!);
            return true;
        }
    }

    public DeltaOutcome ApplyDelta(string key, ulong delta, bool increment, out ulong result)
    {
        result = 0;
        ExecuteFlushIfDue();

        lock (StripeFor(key))
        lock (_gate)
        {
            if (!TryGetLiveLocked(key, _clock.GetUtcNow(), out var entry))
                return DeltaOutcome.NotFound;

            var text = Encoding.ASCII.GetString(entry!.Value).Trim();
            if (text.Length == 0 || !ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var current))
                return DeltaOutcome.NonNumeric;

            result = increment
                ? unchecked(current + delta)
                : delta > current ? 0 : current - delta;

            var oldSize = entry.Size;
            entry.Value = Encoding.ASCII.GetBytes(result.ToString(CultureInfo.InvariantCulture));
            entry.CasId = NextCasId();
            _currentBytes += entry.Size - oldSize;
            TouchLruLocked(key);
            return DeltaOutcome.Ok;
        }
    }

    public bool Touch(string key, long exptime)
    {
        ExecuteFlushIfDue();

        lock (StripeFor(key))
        lock (_gate)
        {
            if (!TryGetLiveLocked(key, _clock.GetUtcNow(), out var entry))
                return false;
            entry!.ExpiresAt = ExpiryCalculator.ToExpiresAt(exptime, _clock);
            TouchLruLocked(key);
            return true;
        }
    }

    /// <summary>
    /// <paramref name="delaySeconds"/> &lt;= 0 clears the cache immediately;
    /// a positive delay schedules the clear, as Memcached's
    /// <c>flush_all &lt;delay&gt;</c> does.
    /// </summary>
    public void FlushAll(int delaySeconds)
    {
        lock (_gate)
        {
            if (delaySeconds <= 0)
            {
                ClearLocked();
                _flushDueAt = null;
            }
            else
            {
                _flushDueAt = _clock.GetUtcNow().AddSeconds(delaySeconds);
            }
        }
    }

    /// <summary>Periodic housekeeping: run a due delayed flush and purge expired entries.</summary>
    public void RunMaintenance()
    {
        ExecuteFlushIfDue();

        var now = _clock.GetUtcNow();
        foreach (var key in _items.Keys)
        {
            if (!_items.TryGetValue(key, out var entry) || !entry.IsExpired(now))
                continue;

            lock (StripeFor(key))
            lock (_gate)
            {
                // Revalidate: the entry may have been replaced since the scan.
                if (_items.TryGetValue(key, out var current) && ReferenceEquals(current, entry) && current.IsExpired(now))
                    RemoveLocked(key, current);
            }
        }
    }

    // ---- internals; "Locked" members require _gate (and usually the key's stripe) ----

    private object StripeFor(string key) =>
        _stripes[(key.GetHashCode(StringComparison.Ordinal) & int.MaxValue) % StripeCount];

    private ulong NextCasId() => (ulong)Interlocked.Increment(ref _casCounter);

    /// <summary>Live = present and not expired. An expired entry is removed and reported absent.</summary>
    private bool TryGetLiveLocked(string key, DateTimeOffset now, out CacheEntry? entry)
    {
        if (_items.TryGetValue(key, out entry))
        {
            if (!entry.IsExpired(now))
                return true;
            RemoveLocked(key, entry);
        }
        entry = null;
        return false;
    }

    private StorageOutcome AppendPrependLocked(StorageKind kind, string key, CacheEntry entry, byte[] data)
    {
        if (entry.Value.Length + data.Length > _maxValueBytes ||
            key.Length + (long)entry.Value.Length + data.Length > _maxMemoryBytes)
            return StorageOutcome.TooLarge;

        var combined = new byte[entry.Value.Length + data.Length];
        if (kind == StorageKind.Append)
        {
            entry.Value.CopyTo(combined, 0);
            data.CopyTo(combined, entry.Value.Length);
        }
        else
        {
            data.CopyTo(combined, 0);
            entry.Value.CopyTo(combined, data.Length);
        }

        // Memcached semantics: append/prepend keep the original flags and expiry.
        var oldSize = entry.Size;
        entry.Value = combined;
        entry.CasId = NextCasId();
        _currentBytes += entry.Size - oldSize;
        TouchLruLocked(key);
        EvictWhileOverLimitLocked(protectedKey: key);
        return StorageOutcome.Stored;
    }

    private void EvictWhileOverLimitLocked(string protectedKey)
    {
        while (_currentBytes > _maxMemoryBytes)
        {
            var tail = _lru.Last;
            if (tail is null || tail.Value == protectedKey)
                return;

            var victimKey = tail.Value;
            _lru.Remove(tail);
            _lruNodes.Remove(victimKey);
            if (_items.TryRemove(victimKey, out var victim))
            {
                _currentBytes -= victim.Size;
                _stats.AddEviction();
            }
        }
    }

    private void TouchLruLocked(string key)
    {
        if (_lruNodes.TryGetValue(key, out var node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
        }
        else
        {
            _lruNodes[key] = _lru.AddFirst(key);
        }
    }

    private void RemoveLocked(string key, CacheEntry entry)
    {
        // Only remove if the map still holds this exact entry (an eviction or
        // flush may have beaten us to it, or a newer entry may have replaced it).
        if (!((IDictionary<string, CacheEntry>)_items).Remove(new KeyValuePair<string, CacheEntry>(key, entry)))
            return;

        if (_lruNodes.Remove(key, out var node))
            _lru.Remove(node);
        _currentBytes -= entry.Size;
    }

    private void ClearLocked()
    {
        _items.Clear();
        _lru.Clear();
        _lruNodes.Clear();
        _currentBytes = 0;
    }

    private void ExecuteFlushIfDue()
    {
        lock (_gate)
        {
            if (_flushDueAt is { } due && due <= _clock.GetUtcNow())
            {
                _flushDueAt = null;
                ClearLocked();
            }
        }
    }
}
