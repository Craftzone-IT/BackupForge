using BackupForge.Core.Execution;
using BackupForge.Core.History;
using BackupForge.TestKit;

namespace BackupForge.Core.Tests.Execution;

public class HistoryStoreTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly HistoryStore _h;
    private static readonly DateTimeOffset T0 = new(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);

    public HistoryStoreTests() => _h = new HistoryStore(_dir.Sub("history.db"));

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Start_finish_and_read_back()
    {
        var id = _h.StartRun("j1", RunKinds.Backup, RunTriggers.Schedule, "c-1", T0);
        var o = new RunOutcome { Result = RunResults.Warning, Summary = "3 copied", FilesCopied = 3, BytesTransferred = 1234 };
        o.AddWarning(@"D:\a.txt", "locked");
        o.SqlResults.Add(new SqlDbResult { Instance = ".", Database = "erp", Result = RunResults.Ok, SizeBytes = 99, VerifyOk = true });
        _h.FinishRun(id, T0.AddMinutes(5), o);

        var r = _h.GetRun(id)!;
        Assert.Equal("j1", r.JobId);
        Assert.Equal("c-1", r.ChainRunId);
        Assert.Equal(RunResults.Warning, r.Result);
        Assert.Equal(T0, r.StartedAt);
        Assert.Equal(T0.AddMinutes(5), r.EndedAt);
        Assert.Equal(3, r.FilesCopied);
        Assert.Equal(1, r.WarningCount);
        var item = Assert.Single(_h.GetItems(id));
        Assert.Equal(@"D:\a.txt", item.Path);
        var sql = Assert.Single(_h.SqlResults("j1"));
        Assert.Equal("erp", sql.Result.Database);
        Assert.True(sql.Result.VerifyOk);
    }

    [Fact]
    public void Only_first_50_items_are_stored_but_all_counted()
    {
        var id = _h.StartRun("j1", RunKinds.Backup, RunTriggers.Manual, null, T0);
        var o = new RunOutcome { Result = RunResults.Failed };
        for (var i = 0; i < 120; i++) o.AddError("f" + i, "boom");
        _h.FinishRun(id, T0, o);
        Assert.Equal(50, _h.GetItems(id).Count);
        Assert.Equal(120, _h.GetRun(id)!.ErrorCount);
    }

    [Fact]
    public void Unfinished_runs_become_interrupted()
    {
        var a = _h.StartRun("j1", RunKinds.Backup, RunTriggers.Schedule, null, T0);
        var b = _h.StartRun("j2", RunKinds.Backup, RunTriggers.Schedule, null, T0);
        _h.FinishRun(b, T0.AddMinutes(1), new RunOutcome());
        var ids = _h.MarkUnfinishedInterrupted(T0.AddHours(1), "interrupted (service restart)");
        Assert.Equal([a], ids);
        Assert.Equal(RunResults.Interrupted, _h.GetRun(a)!.Result);
        Assert.Equal(RunResults.Ok, _h.GetRun(b)!.Result);
    }

    [Fact]
    public void Last_run_ignores_skipped_and_last_start_sees_running()
    {
        var a = _h.StartRun("j1", RunKinds.Backup, RunTriggers.Schedule, null, T0);
        _h.FinishRun(a, T0.AddMinutes(1), new RunOutcome { Result = RunResults.Failed });
        _h.RecordSkipped("j1", RunKinds.Backup, RunTriggers.Schedule, null, T0.AddHours(1), "skipped (still running)");
        Assert.Equal(a, _h.LastRun("j1")!.Id);
        _h.StartRun("j1", RunKinds.Backup, RunTriggers.Schedule, null, T0.AddHours(2));
        Assert.Equal(T0.AddHours(2), _h.LastStart("j1"));
        Assert.Null(_h.LastRunWithResult("j1", RunKinds.Backup, RunResults.Ok, RunResults.Warning));
        Assert.Equal(3, _h.RunsSince(T0, "j1").Count);
    }

    [Fact]
    public void Kv_config_changes_notifications_and_purge()
    {
        _h.SetValue("a", "1");
        Assert.Equal("1", _h.GetValue("a"));
        _h.SetValue("a", null);
        Assert.Null(_h.GetValue("a"));
        _h.SetTime("t", T0);
        Assert.Equal(T0, _h.GetTime("t"));

        _h.AddConfigChange(T0, "cli", "job j1 disabled", "{}");
        Assert.Equal("job j1 disabled", _h.LastConfigChange()!.Summary);
        var n = _h.AddNotification(T0, "failure", "j1", RunResults.Failed, "sent", null, "fp1");
        _h.UpdateNotification(n, "failed", "smtp down");
        Assert.Equal("failed", _h.LastNotification("j1", "failure", "fp1")!.Status);
        Assert.Null(_h.LastNotification("j2", "failure"));

        var old = _h.StartRun("j1", RunKinds.Backup, RunTriggers.Manual, null, T0.AddDays(-500));
        _h.FinishRun(old, T0.AddDays(-500), new RunOutcome());
        _h.Purge(T0.AddDays(-400));
        Assert.Null(_h.GetRun(old));
        Assert.NotNull(_h.LastConfigChange());
    }

    [Fact]
    public void Reopening_keeps_data()
    {
        var id = _h.StartRun("j1", RunKinds.Backup, RunTriggers.Manual, null, T0);
        var again = new HistoryStore(_dir.Sub("history.db"));
        Assert.NotNull(again.GetRun(id));
    }
}
