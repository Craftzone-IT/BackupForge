using System.Text;
using BackupForge.Core.Config;
using BackupForge.Core.Execution;
using BackupForge.Core.Status;
using Microsoft.Extensions.Logging;

namespace BackupForge.Service.Components;

/// <summary>Writes status\status.json every minute and on every run state change (STATUS-CONTRACT.md 2.).</summary>
public sealed class StatusWriter : IServiceComponent
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    public const int TargetMaxBytes = 64 * 1024;

    private ServiceRuntime _rt = null!;
    private DateTimeOffset _last = DateTimeOffset.MinValue;
    private volatile bool _dirty = true;
    private readonly Lock _writeLock = new();

    public Task StartAsync(ServiceRuntime runtime, CancellationToken ct)
    {
        _rt = runtime;
        runtime.Coordinator.Changed += e => { if (e.Type != CoordinatorEventType.Progress) _dirty = true; };
        Write();
        return Task.CompletedTask;
    }

    public void ConfigChanged(ServiceRuntime runtime) => _dirty = true;

    public void Tick(ServiceRuntime runtime)
    {
        if (_dirty || runtime.Clock.UtcNow - _last >= Interval) Write();
    }

    public Task StopAsync(ServiceRuntime runtime)
    {
        Write();
        return Task.CompletedTask;
    }

    public void Write()
    {
        lock (_writeLock)
        {
            try
            {
                _dirty = false;
                _last = _rt.Clock.UtcNow;
                var json = Build();
                AtomicFile.Write(_rt.Paths.StatusFile, new UTF8Encoding(false).GetBytes(json));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _dirty = true;
                _rt.Loggers.CreateLogger("BackupForge.Status").LogWarning("status.json could not be written: {Error}", ex.Message);
            }
        }
    }

    /// <summary>Serialized, masked status; error lists shrink when the file would exceed ~64 KB.</summary>
    public string Build()
    {
        var doc = _rt.BuildStatus();
        var json = _rt.Masker.Apply(StatusJson.Serialize(doc));
        if (Encoding.UTF8.GetByteCount(json) <= TargetMaxBytes) return json;
        foreach (var j in doc.Jobs)
            if (j.LastRun is { } lr) lr.Errors = [.. lr.Errors.Take(3).Select(e => StatusBuilder.Truncate(e, 120)!)];
        return _rt.Masker.Apply(StatusJson.Serialize(doc));
    }
}
