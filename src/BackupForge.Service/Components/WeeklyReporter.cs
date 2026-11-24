using BackupForge.Core.Notifications;
using BackupForge.Core.Reports;
using BackupForge.Core.Scheduling;
using Microsoft.Extensions.Logging;

namespace BackupForge.Service.Components;

/// <summary>Sends the weekly report at notifications.email.weekly_report (e.g. "Sun 06:30"); missed ones are sent once after start.</summary>
public sealed class WeeklyReporter(Notifier notifier) : IServiceComponent
{
    public const string LastKey = "weekly_report_last";
    private ServiceRuntime _rt = null!;

    public Task StartAsync(ServiceRuntime runtime, CancellationToken ct)
    {
        _rt = runtime;
        if (runtime.History.GetTime(LastKey) is null) runtime.History.SetTime(LastKey, runtime.Clock.UtcNow); // not right after install
        return Task.CompletedTask;
    }

    public void Tick(ServiceRuntime runtime)
    {
        var mail = runtime.ConfigStore.Current.Notifications.Email;
        if (!mail.Enabled || !TimeSpec.TryParseRecurrence(mail.WeeklyReport, out var schedule)) return;
        var now = runtime.Clock.UtcNow;
        var due = ScheduleCalculator.Previous(schedule, now, runtime.Clock.LocalZone);
        var last = runtime.History.GetTime(LastKey);
        if (due is null || (last is not null && last >= due)) return;
        runtime.History.SetTime(LastKey, now);
        var msg = Build();
        notifier.Enqueue(NotificationKinds.WeeklyReport, null, null, msg, null);
        runtime.Loggers.CreateLogger("BackupForge.Report").LogInformation("weekly report queued");
    }

    public MailMessage Build() => WeeklyReport.Build(_rt.ConfigStore.Current, _rt.History, _rt.BuildStatus(), _rt.Clock.UtcNow, _rt.Clock.LocalZone);
}
