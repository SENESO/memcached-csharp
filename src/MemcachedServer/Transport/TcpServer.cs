using System.Net;
using System.Net.Sockets;
using MemcachedServer.Core;
using MemcachedServer.Options;
using MemcachedServer.Protocol;
using Microsoft.Extensions.Logging;

namespace MemcachedServer.Transport;

/// <summary>
/// The listening socket and accept loop. Each accepted connection becomes a
/// <see cref="MemcachedSession"/> running on its own task; this class owns
/// connection accounting and nothing about the protocol.
/// </summary>
public sealed class TcpServer
{
    private readonly ServerOptions _options;
    private readonly CommandProcessor _processor;
    private readonly CacheStats _stats;
    private readonly ILogger<TcpServer> _logger;

    public TcpServer(ServerOptions options, CommandProcessor processor, CacheStats stats, ILogger<TcpServer> logger)
    {
        _options = options;
        _processor = processor;
        _stats = stats;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Loopback, _options.Port);
        listener.Start();
        _logger.LogInformation("Memcached server listening on 127.0.0.1:{Port}", _options.Port);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken);
                _stats.ConnectionOpened();
                _ = RunSessionAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested.
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task RunSessionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var remote = client.Client.RemoteEndPoint;
        _logger.LogDebug("Client connected: {RemoteEndPoint}", remote);
        try
        {
            using (client)
            {
                var session = new MemcachedSession(
                    new StreamDuplexPipe(client.GetStream()), _processor, _stats, _logger);
                await session.RunAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Connection from {RemoteEndPoint} failed.", remote);
        }
        finally
        {
            _stats.ConnectionClosed();
            _logger.LogDebug("Client disconnected: {RemoteEndPoint}", remote);
        }
    }
}
