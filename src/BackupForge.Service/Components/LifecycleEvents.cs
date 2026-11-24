using BackupForge.Core.Abstractions;
using BackupForge.Core.Notifications;
using Microsoft.Extensions.Logging;

namespace BackupForge.Service.Components;

/// <summary>Service start / stop in the event log and VSS orphan cleanup at start (SPEC 6.6, 9.2).</summary>
public sealed class LifecycleEvents(IEventSink events, ISnapshotProvider vss) : IServiceComponent
{
    public Task StartAsync(ServiceRuntime runtime, CancellationToken ct)
    {
        events.Write(EventLevel.Information, EventIds.ServiceStarted, $"BackupForge {ServiceRuntime.AppVersion} started ({runtime.ConfigStore.Current.Jobs.Count} jobs)");
        var orphans = runtime.History.UndeletedVssSnapshots();
        if (orphans.Count > 0 && vss.IsSupported)
        {
            var deleted = vss.DeleteOrphans(orphans);
            foreach (var id in deleted) runtime.History.MarkVssDeleted(id, runtime.Clock.UtcNow);
            // snapshots that no longer exist (auto-released) also count as cleaned up
            foreach (var id in orphans.Except(deleted)) runtime.History.MarkVssDeleted(id, runtime.Clock.UtcNow);
            runtime.Loggers.CreateLogger("BackupForge.Vss").LogInformation("orphaned VSS snapshots cleaned up: {Count}", orphans.Count);
            events.Write(EventLevel.Information, EventIds.VssCleanup, $"orphaned VSS snapshots cleaned up: {string.Join(", ", orphans)}");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(ServiceRuntime runtime)
    {
        events.Write(EventLevel.Information, EventIds.ServiceStopped, "BackupForge stopped");
        return Task.CompletedTask;
    }
}
