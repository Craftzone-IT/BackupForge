using BackupForge.Core.Execution;
using BackupForge.Core.Notifications;
using Microsoft.Extensions.Logging;

namespace BackupForge.Service.Components;

/// <summary>
/// heartbeat_url (SPEC 10.4, healthchecks.io compatible): GET &lt;url&gt; after every successful run and hourly,
/// GET &lt;url&gt;/fail after a failed run. The URL itself is treated as a secret (never logged).
/// </summary>
public sealed class Heartbeat(HttpMessageHandler? handler = null, IEventSink? events = null) : IServiceComponent
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private readonly HttpClient _http = new(handler ?? new SocketsHttpHandler()) { Timeout = TimeSpan.FromSeconds(15) };
    private ServiceRuntime _rt = null!;
    private DateTimeOffset _last = DateTimeOffset.MinValue;
    private Task _pending = Task.CompletedTask;

    public Task Pending => _pending;

    public Task StartAsync(ServiceRuntime runtime, CancellationToken ct)
    {
        _rt = runtime;
        runtime.Coordinator.Changed += e =>
        {
            if (e.Type != CoordinatorEventType.Finished || e.Record is null || e.Kind != RunKinds.Backup) return;
            if (RunResults.IsSuccess(e.Record.Result)) Ping(fail: false);
            else if (e.Record.Result == RunResults.Failed) Ping(fail: true);
        };
        return Task.CompletedTask;
    }

    public void Tick(ServiceRuntime runtime)
    {
        if (runtime.Clock.UtcNow - _last >= Interval) Ping(fail: false);
    }

    private void Ping(bool fail)
    {
        var url = _rt.ConfigStore.Current.Notifications.HeartbeatUrl;
        if (string.IsNullOrWhiteSpace(url)) return;
        _rt.Masker.Register(url);
        _last = _rt.Clock.UtcNow;
        var target = fail ? url.TrimEnd('/') + "/fail" : url;
        var previous = _pending;
        _pending = Task.Run(async () =>
        {
            await previous.ConfigureAwait(false);
            try
            {
                using var resp = await _http.GetAsync(target).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) Report($"HTTP {(int)resp.StatusCode}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                Report(ex.GetType().Name);
            }
        });
    }

    private void Report(string what)
    {
        _rt.Loggers.CreateLogger("BackupForge.Heartbeat").LogWarning("heartbeat ping failed: {What}", what);
        events?.Write(EventLevel.Warning, EventIds.HeartbeatFailed, "heartbeat ping failed: " + what);
    }
}
