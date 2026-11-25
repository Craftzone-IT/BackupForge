using System.Text.Json.Nodes;
using BackupForge.Core.Config;
using BackupForge.Core.Execution;
using BackupForge.Ipc;
using BackupForge.TestKit;

namespace BackupForge.Service.Tests;

[Collection("cli")] // BfCli.Connect is static
public class ServiceRuntimeTests
{
    private static BackupConfig Cfg() => TestConfigs.Jobs(2, c =>
    {
        c.Jobs[0].Schedule = new ScheduleConfig { Kind = ScheduleKinds.Daily, Times = ["10:30"] };
        c.Jobs[0].OnSuccess = ["j2"];
        c.Jobs[1].Schedule = new ScheduleConfig { Kind = ScheduleKinds.Chained };
    });

    [Fact]
    public async Task Scheduled_run_happens_and_cli_sees_it()
    {
        await using var h = await new RuntimeHarness(Cfg()).StartAsync();
        h.Clock.Advance(TimeSpan.FromMinutes(31));
        h.Runtime.Tick();
        await WaitUntil(() => h.Runtime.History.LastRun("j2") is not null);

        var (code, output, _) = await h.Bf("status");
        Assert.Equal(0, code);
        Assert.Contains("j1", output, StringComparison.Ordinal);
        Assert.Contains("ok", output, StringComparison.Ordinal);

        var hist = await h.Bf("history", "--last", "5");
        Assert.Equal(0, hist.Code);
        Assert.Contains("schedule", hist.Out, StringComparison.Ordinal);
        Assert.Contains("chain", hist.Out, StringComparison.Ordinal);

        var jobs = await h.Bf("jobs");
        Assert.Contains("naponta 10:30", jobs.Out, StringComparison.Ordinal);
        Assert.Contains("láncban: j1 után", jobs.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Manual_run_with_wait_returns_exit_code()
    {
        await using var h = await new RuntimeHarness(Cfg()).StartAsync();
        var ok = await h.Bf("run", "j2", "--wait");
        Assert.Equal(0, ok.Code);
        Assert.Contains("j2: run", ok.Out, StringComparison.Ordinal);

        h.Engine.AutoResult = _ => new RunOutcome { Result = RunResults.Warning, Summary = "1 skipped" };
        Assert.Equal(10, (await h.Bf("run", "j2", "--wait")).Code);
        h.Engine.AutoResult = _ => new RunOutcome { Result = RunResults.Failed, Summary = "target offline" };
        Assert.Equal(11, (await h.Bf("run", "j2", "--wait")).Code);
        Assert.Equal(0, (await h.Bf("run", "j2")).Code); // fire and forget
    }

    [Fact]
    public async Task Run_log_and_history_get()
    {
        await using var h = await new RuntimeHarness(Cfg()).StartAsync();
        await h.Bf("run", "j2", "--wait");
        var run = h.Runtime.History.LastRun("j2")!;
        var log = await h.Bf("log", run.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(0, log.Code);
        Assert.Contains("result: ok", log.Out, StringComparison.Ordinal);
        await using var c = await h.Connect();
        var node = await c.CallAsync(IpcOps.HistoryGet, new JsonObject { ["run_id"] = run.Id });
        Assert.Equal("j2", node!["job_id"]!.ToString());
    }

    [Fact]
    public async Task Usage_and_unknown_job_errors()
    {
        await using var h = await new RuntimeHarness(Cfg()).StartAsync();
        Assert.Equal(2, (await h.Bf("frobnicate")).Code);
        Assert.Equal(2, (await h.Bf("run")).Code);
        var r = await h.Bf("run", "nope", "--wait");
        Assert.Equal(1, r.Code);
        Assert.Contains("unknown job", r.Err, StringComparison.Ordinal);
        Assert.Equal(1, (await h.Bf("cancel", "j1")).Code);
    }

    [Fact]
    public async Task Service_not_running_gives_exit_3()
    {
        await using var h = new RuntimeHarness(Cfg()); // not started
        Cli.BfCli.Connect = () => IpcClient.ConnectAsync("bf-nobody-" + Guid.NewGuid().ToString("N")[..6], TimeSpan.FromMilliseconds(300));
        var o = new StringWriter();
        var e = new StringWriter();
        Assert.Equal(3, await Cli.BfCli.RunAsync(["status"], o, e, new StringReader("")));
    }

    [Fact]
    public async Task Invalid_config_on_reload_keeps_last_valid_and_reports()
    {
        await using var h = await new RuntimeHarness(Cfg()).StartAsync();
        var broken = Cfg();
        broken.Jobs[0].OnSuccess = ["ghost"];
        AtomicFile.Write(h.Paths.ConfigFile, ConfigJson.ToUtf8(broken));
        var r = await h.Bf("config", "reload");
        Assert.Equal(1, r.Code);
        Assert.Contains("ghost", r.Err, StringComparison.Ordinal);
        Assert.Equal(["j2"], h.Runtime.ConfigStore.Current.Jobs[0].OnSuccess);
        var status = await h.Bf("status", "--json");
        Assert.Contains("\"config_ok\": false", status.Out, StringComparison.Ordinal);

        var fixedCfg = Cfg();
        fixedCfg.Jobs[1].Enabled = false;
        AtomicFile.Write(h.Paths.ConfigFile, ConfigJson.ToUtf8(fixedCfg));
        Assert.Equal(0, (await h.Bf("config", "reload")).Code);
        Assert.False(h.Runtime.ConfigStore.Current.Jobs[1].Enabled);
        Assert.Contains("DISABLED", h.Runtime.History.LastConfigChange()!.Summary, StringComparison.Ordinal);
        Assert.NotNull(h.Runtime.ConfigStore.DisabledSince("j2"));
    }

    [Fact]
    public async Task Config_save_validates_and_keeps_backups()
    {
        await using var h = await new RuntimeHarness(Cfg()).StartAsync();
        await using var c = await h.Connect();
        var bad = Cfg();
        bad.Jobs[0].Files!.DeleteGuardPct = 0;
        var ex = await Assert.ThrowsAsync<IpcException>(() => c.CallAsync(IpcOps.ConfigSave, new JsonObject { ["config"] = ConfigJson.Serialize(bad) }));
        Assert.Equal(IpcErrorCodes.Invalid, ex.Code);
        Assert.Contains("delete_guard_pct", ex.Details!.ToJsonString(), StringComparison.Ordinal);

        for (var i = 0; i < 12; i++)
        {
            var good = Cfg();
            good.InstanceName = "SRV" + i;
            await c.CallAsync(IpcOps.ConfigSave, new JsonObject { ["config"] = ConfigJson.Serialize(good) });
        }
        Assert.Equal("SRV11", ConfigJson.Load(h.Paths.ConfigFile).InstanceName);
        Assert.Equal("SRV10", ConfigJson.Load(h.Paths.ConfigFile + ".bak.1").InstanceName);
        Assert.True(File.Exists(h.Paths.ConfigFile + ".bak.10"));
        Assert.False(File.Exists(h.Paths.ConfigFile + ".bak.11"));
    }

    [Fact]
    public async Task Secrets_are_write_only()
    {
        await using var h = await new RuntimeHarness(Cfg()).StartAsync();
        await using var c = await h.Connect();
        await c.CallAsync(IpcOps.SecretsSet, new JsonObject { ["ref"] = "target:nas1", ["value"] = "test-password-123" });
        var list = await c.CallAsync(IpcOps.SecretsList);
        Assert.Contains("target:nas1", list!.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("test-password-123", list.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal("test-password-123", h.Secrets.Get("target:nas1"));
        var cfg = await c.CallAsync(IpcOps.ConfigGet);
        Assert.DoesNotContain("test-password-123", cfg!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restart_marks_unfinished_run_interrupted()
    {
        var h = await new RuntimeHarness(Cfg()).StartAsync();
        var id = h.Runtime.History.StartRun("j1", RunKinds.Backup, RunTriggers.Schedule, null, h.Clock.UtcNow);
        await h.Runtime.StopAsync();
        var again = new ServiceRuntime(h.Paths, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, h.Clock, [h.Engine], h.Secrets, new Core.Logging.SecretMasker(), new ServiceOptions { StartIpc = false });
        await again.StartAsync(CancellationToken.None);
        Assert.Equal(RunResults.Interrupted, again.History.GetRun(id)!.Result);
        await again.StopAsync();
        h.Dir.Dispose();
    }

    [Fact]
    public async Task Protocol_mismatch_is_rejected_clearly()
    {
        await using var h = await new RuntimeHarness(Cfg()).StartAsync();
        using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", h.Pipe, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        var w = new StreamWriter(pipe) { AutoFlush = true };
        await w.WriteLineAsync("{\"v\":99,\"id\":1,\"op\":\"status.get\"}");
        var line = await new StreamReader(pipe).ReadLineAsync();
        Assert.Contains("protocol_mismatch", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Events_are_pushed_to_subscribers()
    {
        await using var h = await new RuntimeHarness(Cfg()).StartAsync();
        await using var c = await h.Connect();
        var events = new List<string>();
        c.EventReceived += e => { lock (events) events.Add(e.Event); };
        await c.CallAsync(IpcOps.EventsSubscribe);
        await c.CallAsync(IpcOps.JobRun, new JsonObject { ["job_id"] = "j2", ["wait"] = true });
        await WaitUntil(() => { lock (events) return events.Contains("run.finished"); });
        lock (events) Assert.Contains("run.started", events);
    }

    internal static async Task WaitUntil(Func<bool> cond)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!cond())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(10);
        }
    }
}
