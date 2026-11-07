namespace MemcachedServer.Protocol;

/// <summary>Wire constants of the Memcached text protocol.</summary>
public static class TextProtocol
{
    public const string Version = "1.0.0";

    public const string Stored = "STORED";
    public const string NotStored = "NOT_STORED";
    public const string Exists = "EXISTS";
    public const string NotFound = "NOT_FOUND";
    public const string Deleted = "DELETED";
    public const string Touched = "TOUCHED";
    public const string Ok = "OK";
    public const string End = "END";
    public const string Error = "ERROR";
    public const string BadCommandLine = "CLIENT_ERROR bad command line format";
    public const string BadDataChunk = "CLIENT_ERROR bad data chunk";
    public const string NonNumericDelta = "CLIENT_ERROR cannot increment or decrement non-numeric value";
    public const string ObjectTooLarge = "SERVER_ERROR object too large for cache";
}
