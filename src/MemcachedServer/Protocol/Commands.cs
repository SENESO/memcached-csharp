namespace MemcachedServer.Protocol;

public enum CommandKind
{
    Get,
    Gets,
    Set,
    Add,
    Replace,
    Append,
    Prepend,
    Cas,
    Delete,
    Incr,
    Decr,
    Touch,
    FlushAll,
    Version,
    Stats,
    Quit,
    Unknown
}

/// <summary>
/// A fully parsed command line. For storage commands the data block is not
/// part of this object — the session reads exactly <see cref="Bytes"/> + CRLF
/// from the wire next and hands the payload to the processor.
/// </summary>
public sealed class ParsedCommand
{
    public required CommandKind Kind { get; init; }

    /// <summary>Keys for <c>get</c>/<c>gets</c>.</summary>
    public string[] Keys { get; init; } = Array.Empty<string>();

    /// <summary>The key for single-key commands.</summary>
    public string Key { get; init; } = string.Empty;

    public uint Flags { get; init; }

    public long Exptime { get; init; }

    /// <summary>Payload length declared by a storage command.</summary>
    public int Bytes { get; init; }

    public ulong CasId { get; init; }

    public ulong Delta { get; init; }

    public int FlushDelay { get; init; }

    public bool NoReply { get; init; }

    public bool IsStorage => Kind is CommandKind.Set or CommandKind.Add or CommandKind.Replace
        or CommandKind.Append or CommandKind.Prepend or CommandKind.Cas;
}

/// <summary>Outcome of parsing one command line: either a command, or a ready-to-send error reply.</summary>
public sealed record ParseResult(ParsedCommand? Command, string? ErrorReply, bool CloseConnection)
{
    public static ParseResult Ok(ParsedCommand command) => new(command, null, false);
    public static ParseResult Error(string reply, bool closeConnection = false) => new(null, reply, closeConnection);
}
