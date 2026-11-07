using System.IO.Pipelines;
using System.Text;
using MemcachedServer.Core;
using MemcachedServer.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace MemcachedServer.Tests;

/// <summary>
/// In-memory full-duplex transport: two <see cref="Pipe"/>s cross-connected,
/// so protocol tests exercise the real session/framing code end to end
/// without a socket — fast, deterministic, and hermetic in any environment.
/// </summary>
internal sealed class TestDuplexPipe(PipeReader input, PipeWriter output) : IDuplexPipe
{
    public PipeReader Input { get; } = input;
    public PipeWriter Output { get; } = output;
}

/// <summary>The client end of a test connection: sends raw protocol text, asserts exact replies.</summary>
internal sealed class TestClient
{
    private readonly IDuplexPipe _pipe;
    private readonly List<byte> _received = new();
    private bool _closed;

    public TestClient(IDuplexPipe pipe) => _pipe = pipe;

    public async Task SendAsync(string text)
    {
        await _pipe.Output.WriteAsync(Encoding.ASCII.GetBytes(text));
    }

    /// <summary>Reads until the next bytes exactly match <paramref name="expected"/>, then consumes them.</summary>
    public Task ExpectAsync(string expected, int timeoutMs = 5000) =>
        ExpectBytesAsync(Encoding.ASCII.GetBytes(expected), timeoutMs, expected);

    /// <summary>Byte-exact variant of <see cref="ExpectAsync"/> for binary payloads.</summary>
    public async Task ExpectBytesAsync(byte[] expectedBytes, int timeoutMs = 5000, string? display = null)
    {
        var expected = display ?? $"{expectedBytes.Length} bytes";
        using var timeout = new CancellationTokenSource(timeoutMs);
        while (!StartsWith(expectedBytes))
        {
            if (_received.Count >= expectedBytes.Length)
                throw new Xunit.Sdk.XunitException(
                    $"Expected \"{Escape(expected)}\" but the reply starts with \"{Escape(CurrentText())}\".");
            if (_closed)
                throw new Xunit.Sdk.XunitException(
                    $"The session closed the connection; received: \"{Escape(CurrentText())}\".");
            try
            {
                await FillAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                throw new Xunit.Sdk.XunitException(
                    $"Timed out waiting for \"{Escape(expected)}\"; received so far: \"{Escape(CurrentText())}\".");
            }
        }
        _received.RemoveRange(0, expectedBytes.Length);
    }

    /// <summary>Reads until the accumulated text contains <paramref name="text"/> (for multi-line replies like stats).</summary>
    public async Task ExpectContainsAsync(string text, int timeoutMs = 5000)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);
        while (!CurrentText().Contains(text, StringComparison.Ordinal))
        {
            if (_closed)
                throw new Xunit.Sdk.XunitException(
                    $"The session closed the connection; received: \"{Escape(CurrentText())}\".");
            try
            {
                await FillAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                throw new Xunit.Sdk.XunitException(
                    $"Timed out waiting for the reply to contain \"{Escape(text)}\"; received: \"{Escape(CurrentText())}\".");
            }
        }
    }

    public async Task ExpectNoReplyAsync(int quietMs = 400)
    {
        using var timeout = new CancellationTokenSource(quietMs);
        try
        {
            // A clean connection close (e.g. after "quit") also counts as "no reply".
            while (!_closed)
                await FillAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // Nothing arrived within the window — the expected outcome.
        }
        Assert.Empty(_received);
    }

    private async Task FillAsync(CancellationToken cancellationToken)
    {
        var result = await _pipe.Input.ReadAsync(cancellationToken);
        foreach (var segment in result.Buffer)
            _received.AddRange(segment.ToArray());
        _pipe.Input.AdvanceTo(result.Buffer.End);
        if (result.IsCompleted)
            _closed = true;
    }

    private bool StartsWith(byte[] prefix)
    {
        if (_received.Count < prefix.Length)
            return false;
        for (var i = 0; i < prefix.Length; i++)
        {
            if (_received[i] != prefix[i])
                return false;
        }
        return true;
    }

    private string CurrentText() => Encoding.ASCII.GetString(_received.ToArray());

    private static string Escape(string text) => text.Replace("\r", "\\r").Replace("\n", "\\n");
}

/// <summary>A running session wired to a <see cref="TestClient"/>, with the store and clock exposed to the test.</summary>
internal sealed class SessionHarness : IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdown;
    private readonly Task _sessionTask;

    private SessionHarness(TestClient client, MemcachedStore store, CacheStats stats, FakeTimeProvider clock,
        Task sessionTask, CancellationTokenSource shutdown)
    {
        Client = client;
        Store = store;
        Stats = stats;
        Clock = clock;
        _sessionTask = sessionTask;
        _shutdown = shutdown;
    }

    public TestClient Client { get; }
    public MemcachedStore Store { get; }
    public CacheStats Stats { get; }
    public FakeTimeProvider Clock { get; }

    public static SessionHarness Start(long maxMemoryBytes = 64 * 1024 * 1024, int maxValueBytes = 1024 * 1024)
    {
        var clock = new FakeTimeProvider();
        var stats = new CacheStats();
        var store = new MemcachedStore(clock, stats, maxMemoryBytes, maxValueBytes);
        var processor = new CommandProcessor(store, stats, clock);

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var clientPipe = new TestDuplexPipe(serverToClient.Reader, clientToServer.Writer);
        var serverPipe = new TestDuplexPipe(clientToServer.Reader, serverToClient.Writer);

        var shutdown = new CancellationTokenSource();
        var session = new MemcachedSession(serverPipe, processor, stats, NullLogger.Instance);
        var task = session.RunAsync(shutdown.Token);
        return new SessionHarness(new TestClient(clientPipe), store, stats, clock, task, shutdown);
    }

    /// <summary>True once the session has ended (e.g. after <c>quit</c> or a fatal protocol error).</summary>
    public async Task<bool> SessionEndedAsync(int timeoutMs = 5000)
    {
        try
        {
            await _sessionTask.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        try
        {
            await _sessionTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            // In-memory pipes: a lingering session task cannot outlive the test process meaningfully.
        }
        _shutdown.Dispose();
    }
}
