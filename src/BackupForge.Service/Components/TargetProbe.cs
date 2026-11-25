using System.Collections.Concurrent;
using BackupForge.Core.Abstractions;
using BackupForge.Core.Config;
using BackupForge.Core.Status;
using Microsoft.Extensions.Logging;

namespace BackupForge.Service.Components;

/// <summary>Checks every target (reachable, free / total space) every few minutes for status.json and the GUI.</summary>
public sealed class TargetProbe(IFileSystem fs, ITargetConnector connector) : IServiceComponent
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, TargetStatus> _cache = new(StringComparer.Ordinal);
    private ServiceRuntime _rt = null!;
    private DateTimeOffset _last = DateTimeOffset.MinValue;
    private Task _running = Task.CompletedTask;

    public Task StartAsync(ServiceRuntime runtime, CancellationToken ct)
    {
        _rt = runtime;
        return Task.CompletedTask;
    }

    public void ConfigChanged(ServiceRuntime runtime) => _last = DateTimeOffset.MinValue;

    public void Tick(ServiceRuntime runtime)
    {
        var now = runtime.Clock.UtcNow;
        if (now - _last < Interval || !_running.IsCompleted) return;
        _last = now;
        var targets = runtime.ConfigStore.Current.Targets.ToList();
        _running = Task.Run(() => { foreach (var t in targets) _cache[t.Id] = Probe(t); });
    }

    public TargetStatus? Get(TargetConfig t) => _cache.TryGetValue(t.Id, out var s) && s.Path == t.Path ? Clone(s) : null;

    public TargetStatus Probe(TargetConfig t)
    {
        var st = new TargetStatus { Id = t.Id, Type = t.Type, Path = t.Path, MinFreeGb = t.MinFreeGb, Checked = _rt.Clock.UtcNow };
        try
        {
            using (connector.Connect(t, _rt.Secrets))
            {
                st.Online = fs.DirectoryExists(t.Path);
                if (!st.Online) st.Error = "not reachable";
                if (fs.GetSpace(t.Path) is var (free, total))
                {
                    st.FreeGb = Math.Round(free / 1073741824.0, 1);
                    st.SizeGb = Math.Round(total / 1073741824.0, 1);
                }
            }
        }
        catch (Exception ex) when (ex is TargetConnectException or IOException or UnauthorizedAccessException)
        {
            st.Online = false;
            st.Error = _rt.Masker.Apply(ex.Message);
            _rt.Loggers.CreateLogger("BackupForge.Targets").LogWarning("target {Id}: {Error}", t.Id, st.Error);
        }
        _cache[t.Id] = st;
        return st;
    }

    private static TargetStatus Clone(TargetStatus s) => new()
    {
        Id = s.Id, Type = s.Type, Path = s.Path, Online = s.Online, FreeGb = s.FreeGb, SizeGb = s.SizeGb, MinFreeGb = s.MinFreeGb, Checked = s.Checked, Error = s.Error,
    };
}
