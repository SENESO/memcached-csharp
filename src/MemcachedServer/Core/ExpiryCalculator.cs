namespace MemcachedServer.Core;

/// <summary>
/// Converts a protocol <c>exptime</c> value into an absolute expiry instant,
/// following Memcached's rules:
/// <list type="bullet">
/// <item><c>0</c> — never expires</item>
/// <item><c>1..2592000</c> (up to 30 days) — relative seconds from now</item>
/// <item><c>&gt; 2592000</c> — absolute Unix time (seconds since 1970-01-01 UTC)</item>
/// <item>negative — already expired</item>
/// </list>
/// </summary>
public static class ExpiryCalculator
{
    /// <summary>Exptime values up to 30 days (in seconds) are relative.</summary>
    public const long RelativeLimitSeconds = 60 * 60 * 24 * 30; // 2,592,000

    public static DateTimeOffset? ToExpiresAt(long exptime, TimeProvider clock)
    {
        if (exptime == 0)
            return null;

        if (exptime < 0)
            return DateTimeOffset.UnixEpoch; // any instant in the past: already expired

        if (exptime <= RelativeLimitSeconds)
            return clock.GetUtcNow().AddSeconds(exptime);

        return DateTimeOffset.FromUnixTimeSeconds(exptime);
    }
}
