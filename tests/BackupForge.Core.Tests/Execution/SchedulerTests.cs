using BackupForge.Core.Config;
using BackupForge.Core.Execution;
using BackupForge.TestKit;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupForge.Core.Tests.Execution;

public class SchedulerTests
{
    private static BackupConfig DailyAt(string time, bool catchUp = true) => TestConfigs.Jobs(1, c =>
    {
        c.Jobs[0].Schedule = new ScheduleConfig { Kind = ScheduleKinds.Daily, Times = [time] };
        c.Jobs[0].CatchUp = catchUp;
    });

    [Fact]
    public async Task Due_job_is_queued_on_tick_and_next_is_advanced()
    {
        var cfg = DailyAt("10:30");
        await using var h = new CoordinatorHarness(cfg);
        h.Engine.AutoComplete = true;
        var s = new JobScheduler(h.Clock, h.Coordinator, h.History, NullLogger.Instance);
        s.Load(cfg, startup: true);
        Assert.Equal(h.Clock.UtcNow.AddMinutes(30), s.NextRun("j1"));
        s.Tick();
        Assert.Empty(h.Engine.Started);
        h.Clock.Advance(TimeSpan.FromMinutes(31));
        s.Tick();
        await CoordinatorTests.WaitUntil(() => h.History.LastRun("j1") is not null);
        Assert.Equal(RunTriggers.Schedule, h.History.LastRun("j1")!.Trigger);
        Assert.Equal(new DateTimeOffset(2026, 11, 25, 10, 30, 0, TimeSpan.Zero), s.NextRun("j1"));
        s.Tick();
        await Task.Delay(50);
        Assert.Single(h.Engine.Started);
    }

    [Fact]
    public async Task Catch_up_once_after_delay_when_a_run_was_missed()
    {
        var cfg = DailyAt("08:00");
        await using var h = new CoordinatorHarness(cfg);
        h.Engine.AutoComplete = true;
        // The job was known 3 days ago; last run 2 days ago at 08:00; now it is 10:00 → today's 08:00 was missed.
        h.History.SetTime("seen:job:j1", h.Clock.UtcNow.AddDays(-3));
        var old = h.History.StartRun("j1", RunKinds.Backup, RunTriggers.Schedule, null, h.Clock.UtcNow.AddDays(-2).AddHours(-2));
        h.History.FinishRun(old, h.Clock.UtcNow.AddDays(-2), new RunOutcome());

        var s = new JobScheduler(h.Clock, h.Coordinator, h.History, NullLogger.Instance);
        s.Load(cfg, startup: true);
        Assert.Equal(h.Clock.UtcNow.AddMinutes(2), s.NextRun("j1"));
        s.Tick();
        Assert.Empty(h.Engine.Started);
        h.Clock.Advance(TimeSpan.FromMinutes(2));
        s.Tick();
        await CoordinatorTests.WaitUntil(() => h.History.LastRun("j1")?.Trigger == RunTriggers.CatchUp);
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        s.Tick();
        await Task.Delay(50);
        Assert.Single(h.Engine.Started); // only once
    }

    [Fact]
    public async Task No_catch_up_for_new_job_or_when_disabled_or_already_ran()
    {
        var cfg = DailyAt("08:00");
        await using var h = new CoordinatorHarness(cfg);
        var s = new JobScheduler(h.Clock, h.Coordinator, h.History, NullLogger.Instance);
        s.Load(cfg, startup: true); // first time seen → no catch-up
        Assert.Equal(new DateTimeOffset(2026, 11, 25, 8, 0, 0, TimeSpan.Zero), s.NextRun("j1"));

        var noCatch = DailyAt("08:00", catchUp: false);
        h.History.SetTime("seen:job:j1", h.Clock.UtcNow.AddDays(-3));
        s.Load(noCatch, startup: true);
        Assert.Equal(new DateTimeOffset(2026, 11, 25, 8, 0, 0, TimeSpan.Zero), s.NextRun("j1"));

        var id = h.History.StartRun("j1", RunKinds.Backup, RunTriggers.Schedule, null, h.Clock.UtcNow.AddHours(-2));
        h.History.FinishRun(id, h.Clock.UtcNow.AddHours(-1), new RunOutcome { Result = RunResults.Failed });
        s.Load(cfg, startup: true); // it ran (even failed) after the due time → no catch-up
        Assert.Equal(new DateTimeOffset(2026, 11, 25, 8, 0, 0, TimeSpan.Zero), s.NextRun("j1"));
    }

    [Fact]
    public void Disabled_and_manual_jobs_have_no_schedule()
    {
        var cfg = DailyAt("08:00");
        cfg.Jobs[0].Enabled = false;
        Assert.Empty(JobScheduler.EntriesFor(cfg));
        cfg.Jobs[0].Enabled = true;
        cfg.Jobs[0].Schedule = new ScheduleConfig { Kind = ScheduleKinds.Manual };
        Assert.Empty(JobScheduler.EntriesFor(cfg));
    }

    [Fact]
    public void Maintenance_and_checkdb_entries()
    {
        var c = Config.ConfigFixtures.Example();
        var keys = JobScheduler.EntriesFor(c).Select(e => e.Key).ToList();
        Assert.Contains("job:company-mirror", keys);
        Assert.Contains("job:sql-main", keys);
        Assert.Contains("checkdb:sql-main", keys);
        Assert.Contains("forget_prune:offsite", keys);
        Assert.Contains("check:offsite", keys);
        Assert.DoesNotContain("job:sql-to-offsite", keys); // chained
        c.ResticRepos[0].AppendOnlyServer = true;
        Assert.DoesNotContain("forget_prune:offsite", JobScheduler.EntriesFor(c).Select(e => e.Key));
    }
}
