using BackupForge.Core.Config;
using BackupForge.Core.Execution;
using BackupForge.Core.Notifications;
using BackupForge.TestKit;

namespace BackupForge.Service.Tests;

[Collection("cli")]
public class NotifierTests
{
    private static BackupConfig Cfg() => TestConfigs.Jobs(1, c =>
    {
        c.Language = "hu";
        c.Jobs[0].Schedule = new ScheduleConfig { Kind = ScheduleKinds.Daily, Times = ["22:00"] };
        c.Notifications.Email = new EmailSettings
        {
            Enabled = true, SmtpHost = "smtp.example.com", From = "bf@example.com", To = ["it@example.com"],
            Username = "bf@example.com", PasswordRef = "smtp",
        };
    });

    private static async Task<RuntimeHarness> Start(BackupConfig? cfg = null)
    {
        var h = new RuntimeHarness(cfg ?? Cfg());
        h.Secrets.Set("smtp", "test-smtp-pw-789");
        return await h.StartAsync();
    }

    private static async Task Run(RuntimeHarness h, string result, string summary, bool expectNotification = true)
    {
        h.Engine.AutoResult = _ => new RunOutcome { Result = result, Summary = summary };
        var r = h.Runtime.Coordinator.Enqueue(new WorkRequest { JobId = "j1", Trigger = RunTriggers.Manual });
        await r.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        if (expectNotification && result != RunResults.Ok)
            await ServiceRuntimeTests.WaitUntil(() => h.Runtime.History.LastNotificationAny() is not null);
        await Task.Delay(50);
        await h.FlushMailAsync();
    }

    [Fact]
    public async Task Failure_mail_then_dedupe_then_recovery()
    {
        await using var h = await Start();
        await Run(h, RunResults.Failed, "a cél nem elérhető");
        var mail = Assert.Single(h.Mail.Sent);
        Assert.Equal("[BackupForge][SRV01] HIBA: Job 1 — a cél nem elérhető", mail.Subject);
        Assert.Contains("a cél nem elérhető", mail.Text, StringComparison.Ordinal);
        Assert.Contains("<html", mail.Html, StringComparison.Ordinal);
        Assert.Single(mail.Attachments); // run log
        Assert.Contains(h.Events.Events, e => e.Id == EventIds.JobFailed);

        await Run(h, RunResults.Failed, "a cél nem elérhető"); // same text within 24 h → no mail
        Assert.Single(h.Mail.Sent);
        await Run(h, RunResults.Failed, "más hiba"); // different text → mail
        Assert.Equal(2, h.Mail.Sent.Count);

        await Run(h, RunResults.Ok, "rendben");
        Assert.Equal(3, h.Mail.Sent.Count);
        Assert.StartsWith("[BackupForge][SRV01] HELYREÁLLT: Job 1", h.Mail.Sent[2].Subject, StringComparison.Ordinal);

        await Run(h, RunResults.Ok, "rendben"); // ok after ok → nothing
        Assert.Equal(3, h.Mail.Sent.Count);
    }

    [Fact]
    public async Task Warning_mail_can_be_disabled()
    {
        var cfg = Cfg();
        cfg.Notifications.Email.OnWarning = false;
        await using var h = await Start(cfg);
        await Run(h, RunResults.Warning, "1 fájl kihagyva", expectNotification: false);
        await Task.Delay(100);
        await h.FlushMailAsync();
        Assert.Empty(h.Mail.Sent);
        Assert.Contains(h.Events.Events, e => e.Id == EventIds.JobWarning);
    }

    [Fact]
    public async Task Smtp_failure_is_retried_then_reported()
    {
        await using var h = await Start();
        h.Mail.FailNext = 100;
        await Run(h, RunResults.Failed, "boom");
        Assert.Equal(1, h.Mail.Attempts);
        foreach (var delay in new[] { 1, 5, 15 })
        {
            h.Clock.Advance(TimeSpan.FromMinutes(delay));
            await h.FlushMailAsync();
        }
        Assert.Equal(4, h.Mail.Attempts);
        Assert.Equal(0, h.Runtime.Notifier!.PendingCount);
        Assert.Contains(h.Events.Events, e => e.Id == EventIds.SmtpFailed);
        Assert.Equal("smtp connection refused", h.Runtime.BuildStatus().Notifications.LastError);
    }

    [Fact]
    public async Task Smtp_recovers_on_retry()
    {
        await using var h = await Start();
        h.Mail.FailNext = 1;
        await Run(h, RunResults.Failed, "boom");
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        await h.FlushMailAsync();
        Assert.Single(h.Mail.Sent);
        Assert.Null(h.Runtime.BuildStatus().Notifications.LastError);
    }

    [Fact]
    public async Task Secrets_never_reach_the_mail()
    {
        await using var h = await Start();
        h.Masker.Register("test-smtp-pw-789");
        await Run(h, RunResults.Failed, "login with test-smtp-pw-789 failed");
        var m = Assert.Single(h.Mail.Sent);
        Assert.DoesNotContain("test-smtp-pw-789", m.Subject + m.Text + m.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Watchdog_sends_stale_mail_once_per_day()
    {
        var cfg = Cfg();
        cfg.Jobs[0].Schedule = new ScheduleConfig { Kind = ScheduleKinds.Manual }; // the schedule must not run it
        cfg.Jobs[0].ExpectedIntervalH = 24;
        var h0 = new RuntimeHarness(cfg);
        h0.Secrets.Set("smtp", "test-smtp-pw-789");
        await using var h = await h0.StartAsync();
        h.Runtime.History.SetTime("seen:job:j1", h.Clock.UtcNow);
        // expected 24 h; no successful run for 24 * 1.5 + 1 = 37 h → stale
        h.Clock.Advance(TimeSpan.FromHours(38));
        await h.FlushMailAsync();
        var m = Assert.Single(h.Mail.Sent);
        Assert.Contains("NINCS FRISS MENTÉS", m.Subject, StringComparison.Ordinal);
        Assert.Contains("órája nincs sikeres mentés", m.Text, StringComparison.Ordinal);
        Assert.True(h.Runtime.BuildStatus().Jobs[0].Stale);
        h.Clock.Advance(TimeSpan.FromHours(2));
        await h.FlushMailAsync();
        Assert.Single(h.Mail.Sent);
        h.Clock.Advance(TimeSpan.FromHours(23));
        await h.FlushMailAsync();
        Assert.Equal(2, h.Mail.Sent.Count);
    }

    [Fact]
    public async Task Invalid_config_mail_and_event()
    {
        await using var h = await Start();
        var broken = Cfg();
        broken.Jobs[0].Target!.TargetId = "missing";
        AtomicFile.Write(h.Paths.ConfigFile, ConfigJson.ToUtf8(broken));
        h.Runtime.ReloadConfig("manual-edit");
        foreach (var c in new[] { h.Runtime.Notifier! }) c.ConfigChanged(h.Runtime);
        await h.FlushMailAsync();
        Assert.Contains(h.Mail.Sent, m => m.Subject.Contains("KONFIGURÁCIÓS HIBA", StringComparison.Ordinal));
        Assert.Contains(h.Events.Events, e => e.Id == EventIds.ConfigError);
    }

    [Fact]
    public async Task Mail_disabled_sends_nothing_but_logs_events()
    {
        var cfg = Cfg();
        cfg.Notifications.Email.Enabled = false;
        await using var h = await Start(cfg);
        await Run(h, RunResults.Failed, "boom", expectNotification: false);
        await Task.Delay(100);
        await h.FlushMailAsync();
        Assert.Empty(h.Mail.Sent);
        Assert.Contains(h.Events.Events, e => e.Id == EventIds.JobFailed);
    }
}
