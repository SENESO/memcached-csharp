using MemcachedServer.Core;
using Microsoft.Extensions.Hosting;

namespace MemcachedServer.Hosting;

/// <summary>
/// Drives the store's periodic housekeeping once a second: purge expired
/// entries and execute a delayed <c>flush_all</c> when it comes due. Access-time
/// (lazy) expiry covers correctness on its own — this sweep exists so memory
/// is actually reclaimed for keys nobody asks about again.
/// </summary>
public sealed class MaintenanceService : BackgroundService
{
    private readonly MemcachedStore _store;

    public MaintenanceService(MemcachedStore store) => _store = store;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            _store.RunMaintenance();
    }
}
