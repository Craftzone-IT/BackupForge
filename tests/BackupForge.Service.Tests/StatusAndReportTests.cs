using System.Net;
using BackupForge.Core.Config;
using BackupForge.Core.Execution;
using BackupForge.Core.Status;
using BackupForge.Service.Components;
using BackupForge.TestKit;

namespace BackupForge.Service.Tests;

[Collection("cli")]
public class StatusAndReportTests
{
    /// <summary>The SPEC example (files + sql + restic), with a local target.</summary>
    private static BackupConfig Example(string root)
    {
        var c = ConfigJson.Parse(File.ReadAllText(TestPaths.Example("config.example.json")));
        c.Targets[1].Path = Path.Combine(root, "usb");
        Directory.CreateDirectory(c.Targets[1].Path);
        c.Jobs[0].Target!.TargetId = "usb";
        c.Notifications.Email.PasswordRef = null;
        c.Notifications.Email.Username = null;
        return c;
    }

    private static async Task<RuntimeHarness> Start(Func<string, BackupConfig>? cfg = null)
    {
        var dir = new TempDir();
        var h = new RuntimeHarness(cfg?.Invoke(dir.Path) ?? Example(dir.Path));
        foreach (var s in new[] { "target:nas1", "restic:offsite" }) h.Secrets.Set(s, "test-password-123");
        return await h.StartAsync();
    }

    [Fact]
    public void Contract_example_matches_schema()
    {
        SchemaAssert.Valid("status.schema.json", File.ReadAllText(TestPaths.Example("status.example.json")));
        Assert.NotNull(StatusJson.Parse(File.ReadAllText(TestPaths.Example("status.example.json"))));
    }

    [Fact]
    public async Task Status_file_is_written_and_matches_schema_for_all_job_types()
    {
        await using var h = await Start();
        Assert.True(h.Runtime.ConfigStore.IsFileValid, h.Runtime.ConfigStore.Error);
        h.Engine.AutoResult = ctx => ctx.Request.JobId == "sql-main"
            ? new RunOutcome { Result = RunResults.Warning, Summary = "1 warning" }
            : new RunOutcome { Summary = "ok", FilesCopied = 3 };
        await h.Runtime.Coordinator.Enqueue(new WorkRequest { JobId = "company-mirror", Trigger = RunTriggers.Manual }).Completion;
        await h.Runtime.Coordinator.Enqueue(new WorkRequest { JobId = "sql-main", Trigger = RunTriggers.Manual }).Completion;
        await ServiceRuntimeTests.WaitUntil(() => h.Runtime.History.LastRun("sql-to-offsite") is not null);
        h.Runtime.StatusWriter!.Write();

        var json = File.ReadAllText(h.Paths.StatusFile);
        SchemaAssert.Valid("status.schema.json", json);
        var doc = StatusJson.Parse(json)!;
        Assert.Equal(1, doc.StatusSchema);
        var files = doc.Jobs.Single(j => j.Id == "company-mirror");
        Assert.Equal("ok", files.LastRun!.Result);
        Assert.Equal(3, files.LastRun.FilesCopied);
        Assert.NotNull(files.Target);
        Assert.NotNull(files.Versions);
        Assert.False(files.DeleteGuard!.Tripped);
        Assert.Equal("4 óránként 06:15-től", files.ScheduleText);
        var sql = doc.Jobs.Single(j => j.Id == "sql-main");
        Assert.NotNull(sql.Sql); // empty block before the SQL engine has data
        Assert.Equal(["sql-to-offsite"], sql.Chain!.OnSuccess);
        Assert.Equal(14, sql.ExpectedIntervalH);
        var restic = doc.Jobs.Single(j => j.Id == "sql-to-offsite");
        Assert.Equal("chain", restic.LastRun!.Trigger);
        Assert.Equal("sftp:storage.example.com:/srv01", restic.Restic!.LocationDisplay);
        Assert.Equal("láncban: sql-main után", restic.ScheduleText);
        Assert.Equal(14, restic.ExpectedIntervalH);
        Assert.Single(doc.Repos);
        Assert.Equal(2, doc.Targets.Count);
        Assert.DoesNotContain("test-password-123", json, StringComparison.Ordinal);
        Assert.DoesNotContain("backup@storage", json, StringComparison.Ordinal);
        Assert.True(json.Length < StatusWriter.TargetMaxBytes);
        Assert.DoesNotContain(Directory.GetFiles(h.Paths.StatusDir), f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Disabled_job_shows_disabled_since()
    {
        await using var h = await Start();
        var cfg = ConfigJson.Clone(h.Runtime.ConfigStore.Current);
        cfg.Jobs[0].Enabled = false;
        Assert.True(h.Runtime.SaveConfig(cfg, "cli").IsValid);
        var doc = h.Runtime.BuildStatus();
        var j = doc.Jobs[0];
        Assert.False(j.Enabled);
        Assert.NotNull(j.DisabledSince);
        Assert.Null(j.NextRun);
        Assert.NotNull(doc.Service.ConfigChanged);
        var report = h.Runtime.Reporter!.Build();
        Assert.Contains("Letiltott feladatok", report.Html, StringComparison.Ordinal);
        Assert.Contains("DISABLED", report.Html, StringComparison.Ordinal); // in the config change list
        Assert.Contains("FIGYELEM", report.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Weekly_report_sent_once_per_schedule_and_sample_matches()
    {
        await using var h = await Start();
        h.Engine.AutoResult = ctx => ctx.Request.JobId == "company-mirror" ? new RunOutcome { Summary = "312 másolva", BytesTransferred = 1_288_490_188 } : RunOutcome.Failed("a cél nem elérhető");
        await h.Runtime.Coordinator.Enqueue(new WorkRequest { JobId = "company-mirror", Trigger = RunTriggers.Manual }).Completion;
        await h.Runtime.Coordinator.Enqueue(new WorkRequest { JobId = "sql-main", Trigger = RunTriggers.Manual }).Completion;
        await h.FlushMailAsync();
        var before = h.Mail.Sent.Count;
        // 2026-11-24 is a Tuesday; the report is due on Sunday 06:30
        h.Clock.Set(new DateTimeOffset(2026, 11, 29, 6, 31, 0, TimeSpan.Zero));
        await h.FlushMailAsync();
        var reports = h.Mail.Sent.Skip(before).Where(m => m.Subject.Contains("Heti mentési riport", StringComparison.Ordinal)).ToList();
        var report = Assert.Single(reports);
        var sample = Path.Combine(FindRepoRoot(), "docs", "weekly-report-sample.html");
        if (Environment.GetEnvironmentVariable("BF_WRITE_SAMPLES") == "1") File.WriteAllText(sample, report.Html);
        Assert.Contains("HIBA", report.Subject, StringComparison.Ordinal);
        Assert.Contains("Céges adatok", report.Html, StringComparison.Ordinal);
        Assert.Contains(",4 GB", report.Html, StringComparison.Ordinal); // two runs (the manual one + the scheduled one at the jump)
        h.Clock.Advance(TimeSpan.FromHours(1));
        await h.FlushMailAsync();
        Assert.Single(h.Mail.Sent.Skip(before), m => m.Subject.Contains("Heti mentési riport", StringComparison.Ordinal));

        // docs/weekly-report-sample.html is regenerated with: BF_WRITE_SAMPLES=1 dotnet test …
        Assert.True(File.Exists(sample), "docs/weekly-report-sample.html missing — run the tests once with BF_WRITE_SAMPLES=1");
    }

    [Fact]
    public async Task Heartbeat_pings_success_and_fail_urls()
    {
        var handler = new RecordingHandler();
        await using var h = await Start(root =>
        {
            var c = Example(root);
            c.Notifications.HeartbeatUrl = "https://hc-ping.example.com/0000-test-uuid";
            return c;
        });
        var hb = new Heartbeat(handler);
        await hb.StartAsync(h.Runtime, CancellationToken.None);
        hb.Tick(h.Runtime); // hourly ping
        await hb.Pending;
        h.Engine.AutoResult = _ => RunOutcome.Failed("boom");
        await h.Runtime.Coordinator.Enqueue(new WorkRequest { JobId = "company-mirror", Trigger = RunTriggers.Manual }).Completion;
        await Task.Delay(50);
        await hb.Pending;
        h.Engine.AutoResult = null;
        await h.Runtime.Coordinator.Enqueue(new WorkRequest { JobId = "company-mirror", Trigger = RunTriggers.Manual }).Completion;
        await Task.Delay(50);
        await hb.Pending;
        Assert.Equal(["https://hc-ping.example.com/0000-test-uuid", "https://hc-ping.example.com/0000-test-uuid/fail", "https://hc-ping.example.com/0000-test-uuid"], handler.Urls);
        Assert.Equal("***", h.Masker.Apply("https://hc-ping.example.com/0000-test-uuid"));
    }

    [Fact]
    public async Task Report_preview_via_cli()
    {
        await using var h = await Start();
        var r = await h.Bf("report", "preview");
        Assert.Equal(0, r.Code);
        Assert.Contains("<html", r.Out, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "BackupForge.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Urls) Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
