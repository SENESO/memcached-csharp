using MemcachedServer.Transport;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MemcachedServer.Hosting;

/// <summary>Runs the TCP listener for the lifetime of the host and stops it gracefully.</summary>
public sealed class MemcachedHostedService : IHostedService
{
    private readonly TcpServer _server;
    private readonly ILogger<MemcachedHostedService> _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _serverTask;

    public MemcachedHostedService(TcpServer server, ILogger<MemcachedHostedService> logger)
    {
        _server = server;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _serverTask = _server.RunAsync(_shutdown.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _shutdown.CancelAsync();
        if (_serverTask is not null)
        {
            try
            {
                await _serverTask;
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }
        _logger.LogInformation("Memcached server stopped.");
    }
}
