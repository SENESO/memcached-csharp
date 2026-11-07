using System.Buffers;
using System.Globalization;
using System.Text;
using MemcachedServer.Core;

namespace MemcachedServer.Protocol;

/// <summary>
/// Executes parsed commands against the store and renders protocol replies.
/// Stateless apart from the shared store/stats, so one instance serves every
/// connection. Replies are appended to a caller-owned buffer so the session
/// can flush a whole pipelined batch in one write.
/// </summary>
public sealed class CommandProcessor
{
    private static readonly byte[] CrLf = { (byte)'\r', (byte)'\n' };

    private readonly MemcachedStore _store;
    private readonly CacheStats _stats;
    private readonly TimeProvider _clock;
    private readonly DateTimeOffset _startedAt;

    public CommandProcessor(MemcachedStore store, CacheStats stats, TimeProvider clock)
    {
        _store = store;
        _stats = stats;
        _clock = clock;
        _startedAt = clock.GetUtcNow();
    }

    public void Execute(ParsedCommand command, byte[]? data, ArrayBufferWriter<byte> output, out bool closeConnection)
    {
        closeConnection = false;
        switch (command.Kind)
        {
            case CommandKind.Get:
                ExecuteGet(command, includeCas: false, output);
                break;
            case CommandKind.Gets:
                ExecuteGet(command, includeCas: true, output);
                break;
            case CommandKind.Set:
            case CommandKind.Add:
            case CommandKind.Replace:
            case CommandKind.Append:
            case CommandKind.Prepend:
            case CommandKind.Cas:
                ExecuteStorage(command, data ?? Array.Empty<byte>(), output);
                break;
            case CommandKind.Delete:
                ExecuteDelete(command, output);
                break;
            case CommandKind.Incr:
                ExecuteDelta(command, increment: true, output);
                break;
            case CommandKind.Decr:
                ExecuteDelta(command, increment: false, output);
                break;
            case CommandKind.Touch:
                ExecuteTouch(command, output);
                break;
            case CommandKind.FlushAll:
                ExecuteFlushAll(command, output);
                break;
            case CommandKind.Version:
                WriteLine(output, "VERSION " + TextProtocol.Version);
                break;
            case CommandKind.Stats:
                ExecuteStats(output);
                break;
            case CommandKind.Quit:
                closeConnection = true;
                break;
        }
    }

    private void ExecuteGet(ParsedCommand command, bool includeCas, ArrayBufferWriter<byte> output)
    {
        _stats.AddCmdGet();
        foreach (var key in command.Keys)
        {
            var outcome = _store.Get(key);
            if (!outcome.Found)
            {
                _stats.AddGetMiss();
                continue;
            }

            _stats.AddGetHit();
            WriteLine(output, includeCas
                ? FormattableString.Invariant($"VALUE {key} {outcome.Flags} {outcome.Value.Length} {outcome.CasId}")
                : FormattableString.Invariant($"VALUE {key} {outcome.Flags} {outcome.Value.Length}"));
            output.Write(outcome.Value);
            output.Write(CrLf);
        }
        WriteLine(output, TextProtocol.End);
    }

    private void ExecuteStorage(ParsedCommand command, byte[] data, ArrayBufferWriter<byte> output)
    {
        _stats.AddCmdSet();
        var kind = command.Kind switch
        {
            CommandKind.Set => StorageKind.Set,
            CommandKind.Add => StorageKind.Add,
            CommandKind.Replace => StorageKind.Replace,
            CommandKind.Append => StorageKind.Append,
            CommandKind.Prepend => StorageKind.Prepend,
            _ => StorageKind.Cas
        };

        var outcome = _store.Store(kind, command.Key, command.Flags, command.Exptime, data, command.CasId);

        if (command.Kind == CommandKind.Cas)
        {
            switch (outcome)
            {
                case StorageOutcome.Stored: _stats.AddCasHit(); break;
                case StorageOutcome.NotFound: _stats.AddCasMiss(); break;
                case StorageOutcome.Exists: _stats.AddCasBadValue(); break;
            }
        }

        var reply = outcome switch
        {
            StorageOutcome.Stored => TextProtocol.Stored,
            StorageOutcome.NotStored => TextProtocol.NotStored,
            StorageOutcome.Exists => TextProtocol.Exists,
            StorageOutcome.NotFound => TextProtocol.NotFound,
            _ => TextProtocol.ObjectTooLarge
        };

        // noreply silences the normal reply; errors are always sent (Memcached behaviour).
        if (!command.NoReply || outcome == StorageOutcome.TooLarge)
            WriteLine(output, reply);
    }

    private void ExecuteDelete(ParsedCommand command, ArrayBufferWriter<byte> output)
    {
        var deleted = _store.Delete(command.Key);
        if (deleted) _stats.AddDeleteHit(); else _stats.AddDeleteMiss();
        if (!command.NoReply)
            WriteLine(output, deleted ? TextProtocol.Deleted : TextProtocol.NotFound);
    }

    private void ExecuteDelta(ParsedCommand command, bool increment, ArrayBufferWriter<byte> output)
    {
        var outcome = _store.ApplyDelta(command.Key, command.Delta, increment, out var result);
        switch (outcome)
        {
            case DeltaOutcome.Ok:
                if (increment) _stats.AddIncrHit(); else _stats.AddDecrHit();
                if (!command.NoReply)
                    WriteLine(output, result.ToString(CultureInfo.InvariantCulture));
                break;
            case DeltaOutcome.NotFound:
                if (increment) _stats.AddIncrMiss(); else _stats.AddDecrMiss();
                if (!command.NoReply)
                    WriteLine(output, TextProtocol.NotFound);
                break;
            default:
                // Sent even with noreply: it reports a client mistake, not a result.
                WriteLine(output, TextProtocol.NonNumericDelta);
                break;
        }
    }

    private void ExecuteTouch(ParsedCommand command, ArrayBufferWriter<byte> output)
    {
        _stats.AddCmdTouch();
        var touched = _store.Touch(command.Key, command.Exptime);
        if (!command.NoReply)
            WriteLine(output, touched ? TextProtocol.Touched : TextProtocol.NotFound);
    }

    private void ExecuteFlushAll(ParsedCommand command, ArrayBufferWriter<byte> output)
    {
        _stats.AddCmdFlush();
        _store.FlushAll(command.FlushDelay);
        if (!command.NoReply)
            WriteLine(output, TextProtocol.Ok);
    }

    private void ExecuteStats(ArrayBufferWriter<byte> output)
    {
        var now = _clock.GetUtcNow();
        var uptime = (long)(now - _startedAt).TotalSeconds;
        WriteLine(output, "STAT version " + TextProtocol.Version);
        foreach (var (name, value) in _stats.Snapshot(
                     Environment.ProcessId, uptime, now.ToUnixTimeSeconds(), _store.Count, _store.CurrentBytes))
        {
            WriteLine(output, FormattableString.Invariant($"STAT {name} {value}"));
        }
        WriteLine(output, TextProtocol.End);
    }

    private static void WriteLine(ArrayBufferWriter<byte> output, string text)
    {
        output.Write(Encoding.ASCII.GetBytes(text));
        output.Write(CrLf);
    }
}
