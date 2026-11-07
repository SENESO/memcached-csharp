using System.Globalization;

namespace MemcachedServer.Protocol;

/// <summary>
/// Parses one command line (without its CRLF) into a <see cref="ParsedCommand"/>.
/// Pure and allocation-light: no I/O, no store access, fully unit-testable.
///
/// Format rules follow the Memcached text protocol:
/// <c>&lt;command&gt; &lt;key&gt; &lt;flags&gt; &lt;exptime&gt; &lt;bytes&gt; [noreply]</c> for storage,
/// space-separated tokens everywhere, keys of 1–250 bytes with no control characters.
/// </summary>
public static class CommandParser
{
    public const int MaxKeyLength = 250;

    /// <summary>
    /// A storage command declaring more payload than this is rejected and the
    /// connection closed: without a bound, a bogus byte count could make the
    /// server wait (and buffer) forever. Legitimate values are far smaller —
    /// the store's own per-item limit (default 1 MB) rejects oversize items
    /// after their payload has been read, exactly like Memcached.
    /// </summary>
    public const int MaxDataBlockBytes = 128 * 1024 * 1024;

    public static ParseResult Parse(string line)
    {
        if (line.Length == 0)
            return ParseResult.Error(TextProtocol.Error);

        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var name = tokens[0].ToLowerInvariant();

        return name switch
        {
            "get" => ParseRetrieval(tokens, CommandKind.Get),
            "gets" => ParseRetrieval(tokens, CommandKind.Gets),
            "set" => ParseStorage(tokens, CommandKind.Set),
            "add" => ParseStorage(tokens, CommandKind.Add),
            "replace" => ParseStorage(tokens, CommandKind.Replace),
            "append" => ParseStorage(tokens, CommandKind.Append),
            "prepend" => ParseStorage(tokens, CommandKind.Prepend),
            "cas" => ParseStorage(tokens, CommandKind.Cas),
            "delete" => ParseDelete(tokens),
            "incr" => ParseDelta(tokens, CommandKind.Incr),
            "decr" => ParseDelta(tokens, CommandKind.Decr),
            "touch" => ParseTouch(tokens),
            "flush_all" => ParseFlushAll(tokens),
            "version" => ParseResult.Ok(new ParsedCommand { Kind = CommandKind.Version }),
            "stats" => ParseResult.Ok(new ParsedCommand { Kind = CommandKind.Stats }),
            "quit" => ParseResult.Ok(new ParsedCommand { Kind = CommandKind.Quit }),
            _ => ParseResult.Error(TextProtocol.Error),
        };
    }

    private static ParseResult ParseRetrieval(string[] tokens, CommandKind kind)
    {
        if (tokens.Length < 2)
            return BadLine();
        var keys = tokens[1..];
        foreach (var key in keys)
        {
            if (!IsValidKey(key))
                return BadLine();
        }
        return ParseResult.Ok(new ParsedCommand { Kind = kind, Keys = keys });
    }

    private static ParseResult ParseStorage(string[] tokens, CommandKind kind)
    {
        // <cmd> <key> <flags> <exptime> <bytes> [<cas>] [noreply]
        // Token counts include the command name itself.
        var isCas = kind == CommandKind.Cas;
        var fixedCount = isCas ? 6 : 5;
        if (tokens.Length != fixedCount && tokens.Length != fixedCount + 1)
            return BadLine();

        var noReply = false;
        if (tokens.Length == fixedCount + 1)
        {
            if (tokens[^1] != "noreply")
                return BadLine();
            noReply = true;
        }

        if (!IsValidKey(tokens[1]) ||
            !uint.TryParse(tokens[2], NumberStyles.None, CultureInfo.InvariantCulture, out var flags) ||
            !long.TryParse(tokens[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var exptime) ||
            !int.TryParse(tokens[4], NumberStyles.None, CultureInfo.InvariantCulture, out var bytes))
            return BadLine();

        ulong casId = 0;
        if (isCas && !ulong.TryParse(tokens[5], NumberStyles.None, CultureInfo.InvariantCulture, out casId))
            return BadLine();

        if (bytes > MaxDataBlockBytes)
            return ParseResult.Error(TextProtocol.BadCommandLine, closeConnection: true);

        return ParseResult.Ok(new ParsedCommand
        {
            Kind = kind,
            Key = tokens[1],
            Flags = flags,
            Exptime = exptime,
            Bytes = bytes,
            CasId = casId,
            NoReply = noReply
        });
    }

    private static ParseResult ParseDelete(string[] tokens)
    {
        if (tokens.Length is < 2 or > 3 || !IsValidKey(tokens[1]))
            return BadLine();
        var noReply = tokens.Length == 3 && tokens[2] == "noreply";
        if (tokens.Length == 3 && !noReply)
            return BadLine();
        return ParseResult.Ok(new ParsedCommand { Kind = CommandKind.Delete, Key = tokens[1], NoReply = noReply });
    }

    private static ParseResult ParseDelta(string[] tokens, CommandKind kind)
    {
        if (tokens.Length is < 3 or > 4 || !IsValidKey(tokens[1]) ||
            !ulong.TryParse(tokens[2], NumberStyles.None, CultureInfo.InvariantCulture, out var delta))
            return BadLine();
        var noReply = tokens.Length == 4 && tokens[3] == "noreply";
        if (tokens.Length == 4 && !noReply)
            return BadLine();
        return ParseResult.Ok(new ParsedCommand { Kind = kind, Key = tokens[1], Delta = delta, NoReply = noReply });
    }

    private static ParseResult ParseTouch(string[] tokens)
    {
        if (tokens.Length is < 3 or > 4 || !IsValidKey(tokens[1]) ||
            !long.TryParse(tokens[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var exptime))
            return BadLine();
        var noReply = tokens.Length == 4 && tokens[3] == "noreply";
        if (tokens.Length == 4 && !noReply)
            return BadLine();
        return ParseResult.Ok(new ParsedCommand { Kind = CommandKind.Touch, Key = tokens[1], Exptime = exptime, NoReply = noReply });
    }

    private static ParseResult ParseFlushAll(string[] tokens)
    {
        var delay = 0;
        var noReply = false;
        foreach (var token in tokens[1..])
        {
            if (token == "noreply")
                noReply = true;
            else if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedDelay))
                delay = parsedDelay;
            else
                return BadLine();
        }
        return ParseResult.Ok(new ParsedCommand { Kind = CommandKind.FlushAll, FlushDelay = delay, NoReply = noReply });
    }

    private static bool IsValidKey(string key)
    {
        if (key.Length is 0 or > MaxKeyLength)
            return false;
        foreach (var c in key)
        {
            if (c <= ' ' || c == '\x7f')
                return false;
        }
        return true;
    }

    private static ParseResult BadLine() => ParseResult.Error(TextProtocol.BadCommandLine);
}
