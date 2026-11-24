using BackupForge.Core.Abstractions;
using BackupForge.Core.Config;
using BackupForge.Core.Execution;
using BackupForge.Core.History;
using BackupForge.Core.Logging;
using BackupForge.TestKit;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupForge.Core.Tests.Execution;

public sealed class CoordinatorHarness : IAsyncDisposable
{
    public TempDir Dir { get; } = new();
    public ManualClock Clock { get; } = new(new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero));
    public HistoryStore History { get; }
    public FakeEngine Engine { get; } = new();
    public JobCoordinator Coordinator { get; }
    public DataPaths Paths { get; }

    public CoordinatorHarness(BackupConfig config)
    {
        Paths = new DataPaths(Dir.Path);
        History = new HistoryStore(Paths.HistoryDb);
        Coordinator = new JobCoordinator(config, Clock, History, [Engine], new InMemorySecretStore(), Paths, new SecretMasker(), NullLogger.Instance);
    }

    public EnqueueResult Run(string jobId, string trigger = RunTriggers.Manual) =>
        Coordinator.Enqueue(new WorkRequest { JobId = jobId, Trigger = trigger });

    public async ValueTask DisposeAsync()
    {
        await Coordinator.ShutdownAsync(TimeSpan.FromSeconds(5));
        Dir.Dispose();
    }
}

public class CoordinatorTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Runs_a_job_and_records_history()
    {
        await using var h = new CoordinatorHarness(TestConfigs.Jobs(1));
        var r = h.Run("j1");
        Assert.True(r.Accepted);
        await h.Engine.WaitStartedAsync("j1");
        Assert.Single(h.Coordinator.Running);
        h.Engine.Complete("j1", RunResults.Ok, "312 copied");
        var rec = await r.Completion.WaitAsync(Wait);
        Assert.Equal(RunResults.Ok, rec!.Result);
        Assert.Equal("312 copied", rec.Summary);
        Assert.Equal(RunTriggers.Manual, rec.Trigger);
        Assert.True(File.Exists(h.Paths.RunLogFile("j1", rec.Id)));
        Assert.Empty(h.Coordinator.Running);
    }

    [Fact]
    public async Task Global_parallel_limit_is_respected()
    {
        await using var h = new CoordinatorHarness(TestConfigs.Jobs(4, c => c.General.MaxParallelJobs = 2));
        var runs = Enumerable.Range(1, 4).Select(i => h.Run("j" + i)).ToList();
        await h.Engine.WaitStartedAsync("j1");
        await h.Engine.WaitStartedAsync("j2");
        await Task.Delay(100);
        Assert.Equal(2, h.Coordinator.Running.Count);
        Assert.Equal(2, h.Coordinator.Queued.Count);
        h.Engine.Complete("j1");
        await h.Engine.WaitStartedAsync("j3");
        h.Engine.Complete("j2");
        h.Engine.Complete("j3");
        await h.Engine.WaitStartedAsync("j4");
        h.Engine.Complete("j4");
        await Task.WhenAll(runs.Select(r => r.Completion)).WaitAsync(Wait);
        Assert.Equal(2, h.Engine.MaxConcurrent);
    }

    [Fact]
    public async Task Same_target_folder_lock_serializes_and_later_jobs_can_pass()
    {
        // j1 and j2 share a lock (same repository), j3 is independent
        var cfg = TestConfigs.Jobs(1, c =>
        {
            c.General.MaxParallelJobs = 4;
            c.ResticRepos.Add(new ResticRepoConfig { Id = "r", Location = "x", PasswordRef = "p" });
            c.Jobs.Add(new JobConfig { Id = "ra", Name = "a", Type = JobTypes.Restic, Sources = [new SourceConfig { Path = @"C:\a" }], Restic = new ResticJobOptions { RepoId = "r" } });
            c.Jobs.Add(new JobConfig { Id = "rb", Name = "b", Type = JobTypes.Restic, Sources = [new SourceConfig { Path = @"C:\b" }], Restic = new ResticJobOptions { RepoId = "r" } });
        });
        await using var h = new CoordinatorHarness(cfg);
        var a = h.Run("ra");
        await h.Engine.WaitStartedAsync("ra");
        var b = h.Run("rb");
        var j1 = h.Run("j1");
        await h.Engine.WaitStartedAsync("j1");
        Assert.False(h.Engine.IsRunning("rb"));
        h.Engine.Complete("ra");
        await h.Engine.WaitStartedAsync("rb");
        h.Engine.Complete("rb");
        h.Engine.Complete("j1");
        await Task.WhenAll(a.Completion, b.Completion, j1.Completion).WaitAsync(Wait);
    }

    [Fact]
    public void Sql_instance_and_target_locks()
    {
        var c = BackupForge.Core.Tests.Config.ConfigFixtures.Example();
        Assert.Equal(["target:nas1:company"], ResourceLocks.For(c, new WorkRequest { JobId = "company-mirror", Trigger = "manual" }));
        Assert.Equal([@"sql:.\sqlexpress"], ResourceLocks.For(c, new WorkRequest { JobId = "sql-main", Trigger = "manual" }));
        Assert.Equal(["repo:offsite"], ResourceLocks.For(c, new WorkRequest { JobId = "sql-to-offsite", Trigger = "manual" }));
        Assert.Equal(["repo:offsite"], ResourceLocks.For(c, new WorkRequest { JobId = MaintenanceIds.Repo("offsite"), Kind = RunKinds.Check, Trigger = "schedule" }));
    }

    [Fact]
    public async Task Scheduled_run_while_running_is_skipped_and_recorded()
    {
        await using var h = new CoordinatorHarness(TestConfigs.Jobs(1));
        var first = h.Run("j1", RunTriggers.Schedule);
        await h.Engine.WaitStartedAsync("j1");
        var second = h.Run("j1", RunTriggers.Schedule);
        Assert.False(second.Accepted);
        var manual = h.Run("j1");
        Assert.False(manual.Accepted);
        h.Engine.Complete("j1");
        await first.Completion.WaitAsync(Wait);
        var runs = h.History.ListRuns("j1");
        Assert.Contains(runs, r => r.Result == RunResults.Skipped && r.Summary == "skipped (still running)");
        Assert.Equal(2, runs.Count); // the manual attempt is not recorded, it was answered directly
    }

    [Fact]
    public async Task Chain_runs_on_success_and_shares_chain_id()
    {
        var cfg = TestConfigs.Jobs(3, c =>
        {
            c.Jobs[0].OnSuccess = ["j2"];
            c.Jobs[1].Schedule = new ScheduleConfig { Kind = ScheduleKinds.Chained };
            c.Jobs[1].OnSuccess = ["j3"];
            c.Jobs[2].Schedule = new ScheduleConfig { Kind = ScheduleKinds.Chained };
        });
        await using var h = new CoordinatorHarness(cfg);
        h.Run("j1", RunTriggers.Schedule);
        await h.Engine.WaitStartedAsync("j1");
        h.Engine.Complete("j1", RunResults.Warning);
        await h.Engine.WaitStartedAsync("j2");
        h.Engine.Complete("j2");
        await h.Engine.WaitStartedAsync("j3");
        h.Engine.Complete("j3");
        await WaitUntil(() => h.History.LastRun("j3") is not null);
        var r1 = h.History.LastRun("j1")!;
        var r2 = h.History.LastRun("j2")!;
        var r3 = h.History.LastRun("j3")!;
        Assert.NotNull(r1.ChainRunId);
        Assert.Equal(r1.ChainRunId, r2.ChainRunId);
        Assert.Equal(r1.ChainRunId, r3.ChainRunId);
        Assert.Equal(RunTriggers.Chain, r2.Trigger);
    }

    [Fact]
    public async Task Chain_stops_on_failure_and_records_skip()
    {
        var cfg = TestConfigs.Jobs(2, c => { c.Jobs[0].OnSuccess = ["j2"]; c.Jobs[1].Schedule = new ScheduleConfig { Kind = ScheduleKinds.Chained }; });
        await using var h = new CoordinatorHarness(cfg);
        var r = h.Run("j1");
        await h.Engine.WaitStartedAsync("j1");
        h.Engine.Complete("j1", RunResults.Failed, "boom");
        await r.Completion.WaitAsync(Wait);
        await WaitUntil(() => h.History.ListRuns("j2").Count > 0);
        var skip = Assert.Single(h.History.ListRuns("j2"));
        Assert.Equal(RunResults.Skipped, skip.Result);
        Assert.Equal("skipped (chain: j1 failed)", skip.Summary);
        Assert.DoesNotContain("j2", h.Engine.Started);
    }

    [Fact]
    public async Task Dry_run_does_not_trigger_chain()
    {
        var cfg = TestConfigs.Jobs(2, c => c.Jobs[0].OnSuccess = ["j2"]);
        await using var h = new CoordinatorHarness(cfg);
        h.Engine.AutoComplete = true;
        var r = h.Coordinator.Enqueue(new WorkRequest { JobId = "j1", Trigger = RunTriggers.Manual, Options = new RunOptions { DryRun = true } });
        await r.Completion.WaitAsync(Wait);
        await Task.Delay(100);
        Assert.Empty(h.History.ListRuns("j2"));
    }

    [Fact]
    public async Task Disabled_job_is_skipped_when_automatic_but_runs_manually()
    {
        var cfg = TestConfigs.Jobs(1, c => c.Jobs[0].Enabled = false);
        await using var h = new CoordinatorHarness(cfg);
        h.Engine.AutoComplete = true;
        Assert.False(h.Run("j1", RunTriggers.Schedule).Accepted);
        Assert.Equal("skipped (disabled)", h.History.ListRuns("j1").Single().Summary);
        var m = h.Run("j1");
        Assert.True(m.Accepted);
        Assert.Equal(RunResults.Ok, (await m.Completion.WaitAsync(Wait))!.Result);
    }

    [Fact]
    public async Task Cancel_running_gives_interrupted_and_cancel_queued_removes_it()
    {
        await using var h = new CoordinatorHarness(TestConfigs.Jobs(2, c => c.General.MaxParallelJobs = 1));
        var a = h.Run("j1");
        await h.Engine.WaitStartedAsync("j1");
        var b = h.Run("j2");
        Assert.True(h.Coordinator.Cancel("j2"));
        Assert.Null(await b.Completion.WaitAsync(Wait));
        Assert.True(h.Coordinator.Cancel("j1"));
        var rec = await a.Completion.WaitAsync(Wait);
        Assert.Equal(RunResults.Interrupted, rec!.Result);
        Assert.Equal("interrupted (cancelled)", rec.Summary);
        Assert.False(h.Coordinator.Cancel("j1"));
    }

    [Fact]
    public async Task Timeout_fails_the_run()
    {
        var cfg = TestConfigs.Jobs(1);
        await using var h = new CoordinatorHarness(cfg);
        var slow = new SlowEngine(TimeSpan.FromSeconds(30));
        var coord = new JobCoordinator(cfg, h.Clock, h.History, [slow], new InMemorySecretStore(), h.Paths, new SecretMasker(), NullLogger.Instance)
        {
            TimeoutFor = _ => TimeSpan.FromMilliseconds(200),
        };
        var r = coord.Enqueue(new WorkRequest { JobId = "j1", Trigger = RunTriggers.Manual });
        var rec = await r.Completion.WaitAsync(Wait);
        Assert.Equal(RunResults.Failed, rec!.Result);
        Assert.StartsWith("timeout", rec.Summary, StringComparison.Ordinal);
        await coord.ShutdownAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Engine_exception_becomes_failed_with_masked_message()
    {
        var cfg = TestConfigs.Jobs(1);
        await using var h = new CoordinatorHarness(cfg);
        var masker = new SecretMasker();
        masker.Register("test-password-123");
        var coord = new JobCoordinator(cfg, h.Clock, h.History, [new ThrowingEngine("login test-password-123 rejected")], new InMemorySecretStore(), h.Paths, masker, NullLogger.Instance);
        var rec = await coord.Enqueue(new WorkRequest { JobId = "j1", Trigger = RunTriggers.Manual }).Completion.WaitAsync(Wait);
        Assert.Equal(RunResults.Failed, rec!.Result);
        Assert.Equal("error: login *** rejected", rec.Summary);
        Assert.DoesNotContain("test-password-123", File.ReadAllText(h.Paths.RunLogFile("j1", rec.Id)), StringComparison.Ordinal);
        await coord.ShutdownAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task No_engine_for_type_fails_clearly()
    {
        var cfg = TestConfigs.Jobs(1);
        await using var h = new CoordinatorHarness(cfg);
        var coord = new JobCoordinator(cfg, h.Clock, h.History, [], new InMemorySecretStore(), h.Paths, new SecretMasker(), NullLogger.Instance);
        var rec = await coord.Enqueue(new WorkRequest { JobId = "j1", Trigger = RunTriggers.Manual }).Completion.WaitAsync(Wait);
        Assert.Equal(RunResults.Failed, rec!.Result);
        Assert.Contains("no engine", rec.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Shutdown_interrupts_running_and_rejects_new()
    {
        var h = new CoordinatorHarness(TestConfigs.Jobs(1));
        var r = h.Run("j1");
        await h.Engine.WaitStartedAsync("j1");
        await h.Coordinator.ShutdownAsync(TimeSpan.FromSeconds(5));
        var rec = await r.Completion.WaitAsync(Wait);
        Assert.Equal(RunResults.Interrupted, rec!.Result);
        Assert.Equal("interrupted (service stopping)", rec.Summary);
        Assert.False(h.Run("j1").Accepted);
        await h.DisposeAsync();
    }

    [Fact]
    public async Task Unknown_job_rejected()
    {
        await using var h = new CoordinatorHarness(TestConfigs.Jobs(1));
        var r = h.Run("nope");
        Assert.False(r.Accepted);
        Assert.Contains("unknown job", r.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Events_are_raised()
    {
        await using var h = new CoordinatorHarness(TestConfigs.Jobs(1));
        var events = new List<CoordinatorEventType>();
        h.Coordinator.Changed += e => { lock (events) events.Add(e.Type); };
        h.Engine.AutoComplete = true;
        await h.Run("j1").Completion.WaitAsync(Wait);
        lock (events) Assert.Equal([CoordinatorEventType.Started, CoordinatorEventType.Progress, CoordinatorEventType.Finished], events);
    }

    internal static async Task WaitUntil(Func<bool> cond)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!cond())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(10);
        }
    }

    private sealed class SlowEngine(TimeSpan delay) : IJobEngine
    {
        public bool CanRun(string jobType, string kind) => true;
        public async Task<RunOutcome> RunAsync(JobRunContext context, CancellationToken ct)
        {
            await Task.Delay(delay, ct);
            return new RunOutcome();
        }
    }

    private sealed class ThrowingEngine(string message) : IJobEngine
    {
        public bool CanRun(string jobType, string kind) => true;
        public Task<RunOutcome> RunAsync(JobRunContext context, CancellationToken ct) => throw new InvalidOperationException(message);
    }
}
