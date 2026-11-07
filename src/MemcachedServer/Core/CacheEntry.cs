namespace MemcachedServer.Core;

/// <summary>
/// One cached item. Instances are mutated only while holding the owning
/// key's lock stripe inside <see cref="MemcachedStore"/>, and
/// <see cref="Value"/> is never modified in place — appends and updates
/// replace the array — so a reference handed out by a read stays valid.
/// </summary>
public sealed class CacheEntry
{
    public CacheEntry(string key, byte[] value, uint flags, DateTimeOffset? expiresAt, ulong casId)
    {
        Key = key;
        Value = value;
        Flags = flags;
        ExpiresAt = expiresAt;
        CasId = casId;
    }

    public string Key { get; }

    public byte[] Value { get; set; }

    public uint Flags { get; set; }

    /// <summary>Absolute expiry instant, or <c>null</c> for "never expires".</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Unique id assigned on every mutation; used by the <c>cas</c> command.</summary>
    public ulong CasId { get; set; }

    /// <summary>
    /// Bytes this entry counts against the cache limit. Keys are ASCII in
    /// the text protocol, so the string length equals the byte count.
    /// </summary>
    public long Size => Key.Length + Value.LongLength;

    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } expiry && expiry <= now;
}
