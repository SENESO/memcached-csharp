using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using MemcachedServer.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MemcachedServer.Protocol;

/// <summary>
/// One client connection: frames the byte stream into command lines and
/// storage data blocks, dispatches them to the <see cref="CommandProcessor"/>,
/// and flushes replies in batches (pipelined commands included).
///
/// Built on <see cref="PipeReader"/> the same way high-performance .NET
/// servers (Kestrel) are: the buffer is only consumed once a complete unit —
/// a line, or a data block of the declared length + CRLF — is present, so
/// partial network packets need no copying or re-reading.
/// </summary>
public sealed class MemcachedSession
{
    /// <summary>Upper bound for one command line; protects against unbounded buffering.</summary>
    private const int MaxCommandLineBytes = 64 * 1024;

    private readonly IDuplexPipe _transport;
    private readonly CommandProcessor _processor;
    private readonly CacheStats _stats;
    private readonly ILogger _logger;

    private ParsedCommand? _pendingStorage;
    private bool _closeAfterWrite;

    public MemcachedSession(IDuplexPipe transport, CommandProcessor processor, CacheStats stats, ILogger? logger = null)
    {
        _transport = transport;
        _processor = processor;
        _stats = stats;
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var reader = _transport.Input;
        var writer = _transport.Output;
        var output = new ArrayBufferWriter<byte>();
        long leftover = 0;

        try
        {
            while (!_closeAfterWrite)
            {
                var result = await reader.ReadAsync(cancellationToken);
                var buffer = result.Buffer;

                _stats.AddBytesRead(buffer.Length - leftover);

                while (TryProcess(ref buffer, output))
                {
                }

                if (_pendingStorage is null && buffer.Length > MaxCommandLineBytes)
                {
                    WriteLine(output, TextProtocol.Error);
                    buffer = buffer.Slice(buffer.Length);
                    _closeAfterWrite = true;
                }

                reader.AdvanceTo(buffer.Start, buffer.End);
                leftover = buffer.Length;

                if (output.WrittenCount > 0)
                {
                    _stats.AddBytesWritten(output.WrittenCount);
                    await writer.WriteAsync(output.WrittenMemory, cancellationToken);
                    output.ResetWrittenCount();
                }

                if (result.IsCompleted)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Server shutting down — close quietly.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Session ended with an error.");
        }
        finally
        {
            await reader.CompleteAsync();
            await writer.CompleteAsync();
        }
    }

    /// <summary>Consumes one complete unit from <paramref name="buffer"/> if present. Returns false when more bytes are needed.</summary>
    private bool TryProcess(ref ReadOnlySequence<byte> buffer, ArrayBufferWriter<byte> output)
    {
        if (_pendingStorage is not null)
            return TryProcessDataBlock(ref buffer, output);

        var newline = buffer.PositionOf((byte)'\n');
        if (newline is null)
            return false;

        var line = buffer.Slice(buffer.Start, newline.Value);
        buffer = buffer.Slice(buffer.GetPosition(1, newline.Value));
        if (line.Length > 0 && line.Slice(line.Length - 1, 1).FirstSpan[0] == (byte)'\r')
            line = line.Slice(0, line.Length - 1);

        var lineText = line.IsSingleSegment
            ? Encoding.ASCII.GetString(line.FirstSpan)
            : Encoding.ASCII.GetString(line.ToArray());

        var parsed = CommandParser.Parse(lineText);
        if (parsed.ErrorReply is not null)
        {
            WriteLine(output, parsed.ErrorReply);
            if (parsed.CloseConnection)
                _closeAfterWrite = true;
            return true;
        }

        var command = parsed.Command!;
        if (command.IsStorage)
        {
            _pendingStorage = command;
            return true;
        }

        _processor.Execute(command, data: null, output, out var close);
        if (close)
            _closeAfterWrite = true;
        return true;
    }

    private bool TryProcessDataBlock(ref ReadOnlySequence<byte> buffer, ArrayBufferWriter<byte> output)
    {
        var command = _pendingStorage!;
        if (buffer.Length < (long)command.Bytes + 2)
            return false;

        var trailer = buffer.Slice(command.Bytes, 2).ToArray();
        var data = buffer.Slice(0, command.Bytes).ToArray();
        buffer = buffer.Slice(command.Bytes + 2);
        _pendingStorage = null;

        if (trailer[0] != (byte)'\r' || trailer[1] != (byte)'\n')
        {
            // The stream is desynchronised; Memcached reports and closes.
            WriteLine(output, TextProtocol.BadDataChunk);
            _closeAfterWrite = true;
            return true;
        }

        _processor.Execute(command, data, output, out var close);
        if (close)
            _closeAfterWrite = true;
        return true;
    }

    private static void WriteLine(ArrayBufferWriter<byte> output, string text)
    {
        output.Write(Encoding.ASCII.GetBytes(text));
        output.Write(new byte[] { (byte)'\r', (byte)'\n' });
    }
}
