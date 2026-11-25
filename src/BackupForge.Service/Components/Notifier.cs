using BackupForge.Core.Config;
using BackupForge.Core.Execution;
using BackupForge.Core.History;
using BackupForge.Core.Notifications;
using BackupForge.Core.Resources;
using BackupForge.Core.Scheduling;
using BackupForge.Core.Status;
using Microsoft.Extensions.Logging;

namespace BackupForge.Service.Components;

/// <summary>
/// E-mail notifications (SPEC 10.1) with an outbox and retries (1, 5, 15 min), the internal watchdog mail (10.2) and the
/// configuration-error mail. Sending happens on the tick, never on the run's thread.
/// </summary>
public sealed class Notifier(IMailSender sender, IEventSink events) : IServiceComponent
{
    public static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)];

    private readonly List<OutboxItem> _outbox = [];
    private readonly Lock _lock = new();
    private ServiceRuntime _rt = null!;
    private ILogger _log = null!;
    private Task _sending = Task.CompletedTask;
    private string? _lastConfigError;
    private DateTimeOffset _lastWatchdog = DateTimeOffset.MinValue;

    public string? LastError { get; private set; }
    public DateTimeOffset? LastSent { get; private set; }

    private sealed class OutboxItem
    {
        public required MailMessage Message { get; init; }
        public required long NotificationId { get; init; }
        public required string Kind { get; init; }
        public string? JobId { get; init; }
        public int Attempts { get; set; }
        public DateTimeOffset NextAttempt { get; set; }
    }

    public Task StartAsync(ServiceRuntime runtime, CancellationToken ct)
    {
        _rt = runtime;
        _log = runtime.Loggers.CreateLogger("BackupForge.Notify");
        LastSent = runtime.History.LastNotificationAny() is { Status: "sent" } n ? n.At : null;
        runtime.Coordinator.Changed += OnRunEvent;
        CheckConfigError();
        return Task.CompletedTask;
    }

    public void ConfigChanged(ServiceRuntime runtime) => CheckConfigError();

    public NotificationStatus Status() => new()
    {
        EmailEnabled = _rt.ConfigStore.Current.Notifications.Email.Enabled,
        LastSent = LastSent,
        LastError = LastError,
    };

    private void OnRunEvent(CoordinatorEvent e)
    {
        if (e.Type != CoordinatorEventType.Finished || e.Record is null) return;
        try { HandleFinished(e.Record); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _log.LogError(ex, "notification failed"); }
    }

    private void HandleFinished(RunRecord run)
    {
        var cfg = _rt.ConfigStore.Current;
        var mail = cfg.Notifications.Email;
        // event log for every warning / failure
        if (run.Result == RunResults.Warning) events.Write(EventLevel.Warning, EventIds.JobWarning, $"{run.JobId}: {run.Summary}");
        else if (run.Result == RunResults.Failed)
        {
            var guard = run.Kind == RunKinds.Backup && _rt.History.GetValue(DeleteGuardState.TrippedKey(run.JobId)) is not null;
            events.Write(EventLevel.Error, guard ? EventIds.DeleteGuard : EventIds.JobFailed, $"{run.JobId}: {run.Summary}");
        }
        if (!mail.Enabled) return;

        var job = cfg.FindJob(run.JobId);
        var previous = _rt.History.ListRuns(run.JobId, 10, run.Kind)
            .FirstOrDefault(r => r.Id != run.Id && r.IsFinished && r.Result is not RunResults.Skipped && r.StartedAt <= run.StartedAt);
        var kind = NotificationPolicy.KindFor(run, previous, mail);
        if (kind is null) return;
        var fp = NotificationPolicy.Fingerprint(run.JobId, kind, run.Summary);
        if (NotificationPolicy.IsDuplicate(_rt.History, run.JobId, kind, fp, _rt.Clock.UtcNow))
        {
            _rt.History.AddNotification(_rt.Clock.UtcNow, kind, run.JobId, run.Result, "suppressed", "duplicate within 24 h", fp);
            return;
        }
        var jobCfg = job ?? new JobConfig { Id = run.JobId, Name = run.JobId };
        var msg = MailBuilder.ForRun(cfg, kind, jobCfg, run, _rt.History.GetItems(run.Id), _rt.Paths.RunLogFile(run.JobId, run.Id), _rt.Clock.LocalZone);
        Enqueue(kind, run.JobId, run.Result, msg, fp);
    }

    public long Enqueue(string kind, string? jobId, string? result, MailMessage raw, string? fingerprint)
    {
        var masked = new MailMessage
        {
            Subject = _rt.Masker.Apply(raw.Subject),
            Html = _rt.Masker.Apply(raw.Html),
            Text = _rt.Masker.Apply(raw.Text),
        };
        foreach (var a in raw.Attachments)
            masked.Attachments.Add(a with { Content = System.Text.Encoding.UTF8.GetBytes(_rt.Masker.Apply(System.Text.Encoding.UTF8.GetString(a.Content))) });
        var id = _rt.History.AddNotification(_rt.Clock.UtcNow, kind, jobId, result, "pending", null, fingerprint);
        lock (_lock) _outbox.Add(new OutboxItem { Message = masked, NotificationId = id, Kind = kind, JobId = jobId, NextAttempt = _rt.Clock.UtcNow });
        return id;
    }

    /// <summary>Sends immediately and reports the error (GUI / bf notify test).</summary>
    public async Task SendTestAsync(CancellationToken ct)
    {
        var cfg = _rt.ConfigStore.Current;
        var c = Texts.CultureFor(cfg.Language);
        var msg = MailBuilder.Simple(cfg, NotificationKinds.Test, cfg.InstanceName, Texts.Format(c, "mail.test_text", Environment.MachineName));
        var mail = cfg.Notifications.Email;
        if (string.IsNullOrWhiteSpace(mail.SmtpHost) || mail.To.Count == 0) throw new InvalidOperationException("e-mail is not configured (smtp_host / to)");
        try
        {
            await sender.SendAsync(mail, mail.PasswordRef is null ? null : _rt.Secrets.Get(mail.PasswordRef), msg, ct).ConfigureAwait(false);
            _rt.History.AddNotification(_rt.Clock.UtcNow, NotificationKinds.Test, null, null, "sent", null, null);
            LastSent = _rt.Clock.UtcNow;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            var err = _rt.Masker.Apply(ex.Message);
            _rt.History.AddNotification(_rt.Clock.UtcNow, NotificationKinds.Test, null, null, "failed", err, null);
            throw new InvalidOperationException(err);
        }
    }

    public int PendingCount { get { lock (_lock) return _outbox.Count; } }

    /// <summary>Lets tests wait for the current send.</summary>
    public Task Sending => _sending;

    public void Tick(ServiceRuntime runtime)
    {
        var now = runtime.Clock.UtcNow;
        if (now - _lastWatchdog >= TimeSpan.FromMinutes(1))
        {
            _lastWatchdog = now;
            WatchdogCheck(now);
        }
        if (!_sending.IsCompleted) return;
        List<OutboxItem> due;
        lock (_lock) due = [.. _outbox.Where(o => o.NextAttempt <= now)];
        if (due.Count == 0) return;
        _sending = Task.Run(() => SendAllAsync(due));
    }

    private async Task SendAllAsync(List<OutboxItem> items)
    {
        var mail = _rt.ConfigStore.Current.Notifications.Email;
        foreach (var item in items)
        {
            try
            {
                await sender.SendAsync(mail, mail.PasswordRef is null ? null : _rt.Secrets.Get(mail.PasswordRef), item.Message, CancellationToken.None).ConfigureAwait(false);
                lock (_lock) _outbox.Remove(item);
                _rt.History.UpdateNotification(item.NotificationId, "sent", null);
                LastSent = _rt.Clock.UtcNow;
                LastError = null;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                var err = _rt.Masker.Apply(ex.Message);
                item.Attempts++;
                if (item.Attempts > RetryDelays.Length)
                {
                    lock (_lock) _outbox.Remove(item);
                    _rt.History.UpdateNotification(item.NotificationId, "failed", err);
                    LastError = err;
                    events.Write(EventLevel.Error, EventIds.SmtpFailed, $"e-mail could not be sent after {item.Attempts} attempts: {err} ({item.Message.Subject})");
                    _log.LogError("e-mail failed permanently: {Error}", err);
                }
                else
                {
                    item.NextAttempt = _rt.Clock.UtcNow + RetryDelays[item.Attempts - 1];
                    _rt.History.UpdateNotification(item.NotificationId, "retrying", err);
                    _log.LogWarning("e-mail failed (attempt {Attempt}), retry at {Next:o}: {Error}", item.Attempts, item.NextAttempt, err);
                }
            }
        }
    }

    private void CheckConfigError()
    {
        var err = _rt.ConfigStore.Error;
        if (err is null) { _lastConfigError = null; return; }
        if (err == _lastConfigError) return;
        _lastConfigError = err;
        events.Write(EventLevel.Error, EventIds.ConfigError, "config.json: " + err);
        var cfg = _rt.ConfigStore.Current;
        if (!cfg.Notifications.Email.Enabled) return;
        var c = Texts.CultureFor(cfg.Language);
        Enqueue(NotificationKinds.Config, null, null, MailBuilder.Simple(cfg, NotificationKinds.Config, "config.json", Texts.Format(c, "mail.config_text", err)), null);
    }

    /// <summary>SPEC 10.2: no successful run for expected × 1.5 + 1 h → one mail, repeated every 24 h.</summary>
    private void WatchdogCheck(DateTimeOffset now)
    {
        var cfg = _rt.ConfigStore.Current;
        var running = _rt.Coordinator.Running;
        foreach (var job in cfg.Jobs.Where(j => j.Enabled))
        {
            var expected = ExpectedInterval.For(cfg, job);
            if (expected is null) continue;
            var lastOk = _rt.History.LastRunWithResult(job.Id, RunKinds.Backup, RunResults.Ok, RunResults.Warning)?.StartedAt;
            var seen = _rt.History.GetTime("seen:job:" + job.Id);
            var stale = Watchdog.IsStale(job, expected, lastOk, seen, running.Any(r => r.JobId == job.Id), now);
            var key = "stale_mail:" + job.Id;
            if (!stale) { _rt.History.SetTime(key, null); continue; }
            var last = _rt.History.GetTime(key);
            if (last is not null && now - last.Value < NotificationPolicy.StaleRepeat) continue;
            _rt.History.SetTime(key, now);
            var hours = (now - (lastOk ?? seen ?? now)).TotalHours;
            var c = Texts.CultureFor(cfg.Language);
            var text = Texts.Format(c, "mail.stale_text", job.Name, hours, expected.Value);
            events.Write(EventLevel.Error, EventIds.JobStale, text);
            _log.LogWarning("{Text}", text);
            if (cfg.Notifications.Email.Enabled)
                Enqueue(NotificationKinds.Stale, job.Id, null, MailBuilder.Simple(cfg, NotificationKinds.Stale, job.Name, text), null);
        }
    }

    public async Task StopAsync(ServiceRuntime runtime)
    {
        try { await _sending.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
        catch (TimeoutException) { }
    }
}
