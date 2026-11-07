namespace MemcachedServer.Options;

/// <summary>
/// Runtime configuration for the server, populated from command-line
/// arguments in <c>Program.cs</c>:
/// <list type="bullet">
/// <item><c>-p &lt;port&gt;</c> — TCP port to listen on (default 11211)</item>
/// <item><c>-m &lt;megabytes&gt;</c> — maximum cache memory (default 64 MB)</item>
/// <item><c>-I &lt;bytes&gt;</c> — maximum size of a single stored value (default 1 MB)</item>
/// </list>
/// </summary>
public sealed class ServerOptions
{
    public const int DefaultPort = 11211;
    public const int DefaultMaxMemoryMegabytes = 64;
    public const int DefaultMaxValueBytes = 1024 * 1024;

    public int Port { get; init; } = DefaultPort;

    public long MaxMemoryBytes { get; init; } = DefaultMaxMemoryMegabytes * 1024L * 1024L;

    public int MaxValueBytes { get; init; } = DefaultMaxValueBytes;

    public static ServerOptions FromArgs(string[] args)
    {
        var port = DefaultPort;
        long maxMemoryBytes = DefaultMaxMemoryMegabytes * 1024L * 1024L;
        var maxValueBytes = DefaultMaxValueBytes;

        for (var i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "-p" when int.TryParse(args[i + 1], out var parsedPort):
                    port = parsedPort;
                    break;
                case "-m" when long.TryParse(args[i + 1], out var parsedMegabytes) && parsedMegabytes > 0:
                    maxMemoryBytes = parsedMegabytes * 1024L * 1024L;
                    break;
                case "-I" when int.TryParse(args[i + 1], out var parsedItemBytes) && parsedItemBytes > 0:
                    maxValueBytes = parsedItemBytes;
                    break;
            }
        }

        return new ServerOptions
        {
            Port = port,
            MaxMemoryBytes = maxMemoryBytes,
            MaxValueBytes = maxValueBytes
        };
    }
}
