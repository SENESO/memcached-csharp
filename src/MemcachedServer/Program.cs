using MemcachedServer.Core;
using MemcachedServer.Hosting;
using MemcachedServer.Options;
using MemcachedServer.Protocol;
using MemcachedServer.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Command-line options (-p <port>, -m <megabytes>, -I <max item bytes>) are
// parsed by ServerOptions; the host builder is created without the app args
// so its own command-line configuration never sees them.
var options = ServerOptions.FromArgs(args);

var builder = Host.CreateApplicationBuilder();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<CacheStats>();
builder.Services.AddSingleton(sp => new MemcachedStore(
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<CacheStats>(),
    options.MaxMemoryBytes,
    options.MaxValueBytes));
builder.Services.AddSingleton<CommandProcessor>();
builder.Services.AddSingleton<TcpServer>();
builder.Services.AddHostedService<MemcachedHostedService>();
builder.Services.AddHostedService<MaintenanceService>();

var host = builder.Build();
await host.RunAsync();
