using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackupForge.Service;

/// <summary>The hosted service: starts the runtime, ticks it, stops it within the SCM grace time.</summary>
public sealed class Worker(ServiceRuntime runtime, ILogger<Worker> logger) : BackgroundService
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await runtime.StartAsync(cancellationToken).ConfigureAwait(false);
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval);
        do
        {
            try { runtime.Tick(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { logger.LogError(ex, "tick failed"); }
        }
        while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer t, CancellationToken ct)
    {
        try { return await t.WaitForNextTickAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await runtime.StopAsync().ConfigureAwait(false);
    }
}
