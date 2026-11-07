namespace MemcachedServer.Core;

/// <summary>
/// Thread-safe operational counters, exposed through the <c>stats</c> command.
/// All updates use <see cref="Interlocked"/> so hot paths never take a lock
/// just to count something.
/// </summary>
public sealed class CacheStats
{
    private long _cmdGet;
    private long _cmdSet;
    private long _cmdTouch;
    private long _cmdFlush;
    private long _getHits;
    private long _getMisses;
    private long _deleteHits;
    private long _deleteMisses;
    private long _incrHits;
    private long _incrMisses;
    private long _decrHits;
    private long _decrMisses;
    private long _casHits;
    private long _casMisses;
    private long _casBadValue;
    private long _evictions;
    private long _totalItems;
    private long _currConnections;
    private long _totalConnections;
    private long _bytesRead;
    private long _bytesWritten;

    public void AddCmdGet() => Interlocked.Increment(ref _cmdGet);
    public void AddCmdSet() => Interlocked.Increment(ref _cmdSet);
    public void AddCmdTouch() => Interlocked.Increment(ref _cmdTouch);
    public void AddCmdFlush() => Interlocked.Increment(ref _cmdFlush);
    public void AddGetHit() => Interlocked.Increment(ref _getHits);
    public void AddGetMiss() => Interlocked.Increment(ref _getMisses);
    public void AddDeleteHit() => Interlocked.Increment(ref _deleteHits);
    public void AddDeleteMiss() => Interlocked.Increment(ref _deleteMisses);
    public void AddIncrHit() => Interlocked.Increment(ref _incrHits);
    public void AddIncrMiss() => Interlocked.Increment(ref _incrMisses);
    public void AddDecrHit() => Interlocked.Increment(ref _decrHits);
    public void AddDecrMiss() => Interlocked.Increment(ref _decrMisses);
    public void AddCasHit() => Interlocked.Increment(ref _casHits);
    public void AddCasMiss() => Interlocked.Increment(ref _casMisses);
    public void AddCasBadValue() => Interlocked.Increment(ref _casBadValue);
    public void AddEviction() => Interlocked.Increment(ref _evictions);
    public void AddTotalItem() => Interlocked.Increment(ref _totalItems);
    public void AddBytesRead(long count) => Interlocked.Add(ref _bytesRead, count);
    public void AddBytesWritten(long count) => Interlocked.Add(ref _bytesWritten, count);

    public void ConnectionOpened()
    {
        Interlocked.Increment(ref _currConnections);
        Interlocked.Increment(ref _totalConnections);
    }

    public void ConnectionClosed() => Interlocked.Decrement(ref _currConnections);

    public long Evictions => Interlocked.Read(ref _evictions);
    public long TotalItems => Interlocked.Read(ref _totalItems);

    /// <summary>Ordered snapshot in the layout the <c>stats</c> command prints.</summary>
    public IReadOnlyList<(string Name, long Value)> Snapshot(
        int processId, long uptimeSeconds, long unixTimeSeconds, int currItems, long currentBytes)
    {
        return new (string, long)[]
        {
            ("pid", processId),
            ("uptime", uptimeSeconds),
            ("time", unixTimeSeconds),
            ("curr_items", currItems),
            ("total_items", Interlocked.Read(ref _totalItems)),
            ("bytes", currentBytes),
            ("cmd_get", Interlocked.Read(ref _cmdGet)),
            ("cmd_set", Interlocked.Read(ref _cmdSet)),
            ("cmd_touch", Interlocked.Read(ref _cmdTouch)),
            ("cmd_flush", Interlocked.Read(ref _cmdFlush)),
            ("get_hits", Interlocked.Read(ref _getHits)),
            ("get_misses", Interlocked.Read(ref _getMisses)),
            ("delete_hits", Interlocked.Read(ref _deleteHits)),
            ("delete_misses", Interlocked.Read(ref _deleteMisses)),
            ("incr_hits", Interlocked.Read(ref _incrHits)),
            ("incr_misses", Interlocked.Read(ref _incrMisses)),
            ("decr_hits", Interlocked.Read(ref _decrHits)),
            ("decr_misses", Interlocked.Read(ref _decrMisses)),
            ("cas_hits", Interlocked.Read(ref _casHits)),
            ("cas_misses", Interlocked.Read(ref _casMisses)),
            ("cas_badval", Interlocked.Read(ref _casBadValue)),
            ("evictions", Interlocked.Read(ref _evictions)),
            ("curr_connections", Interlocked.Read(ref _currConnections)),
            ("total_connections", Interlocked.Read(ref _totalConnections)),
            ("bytes_read", Interlocked.Read(ref _bytesRead)),
            ("bytes_written", Interlocked.Read(ref _bytesWritten)),
        };
    }
}
