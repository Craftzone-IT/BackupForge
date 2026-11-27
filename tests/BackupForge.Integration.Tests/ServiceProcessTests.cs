using BackupForge.Core.Config;
using BackupForge.Core.History;
using BackupForge.TestKit;

namespace BackupForge.Integration.Tests;

public class ServiceProcessTests
{
    private static BackupConfig Cfg() => TestConfigs.Jobs(2, c =>
    {
        c.Jobs[0].Schedule = new ScheduleConfig { Kind = ScheduleKinds.Interval, EveryMin = 5 };
        c.Jobs[0].OnSuccess = ["j2"];
        c.Jobs[1].Schedule = new ScheduleConfig { Kind = ScheduleKinds.Chained };
    });

    [Fact]
    public async Task Bf_redirected_output_is_utf8_without_bom()
    {
        // CR-002 A7.: offline 'config validate' of a Hungarian config prints "Kötelező mező" to a redirected stderr
        using var d = new TempDir();
        var cfg = TestConfigs.Jobs(1);
        cfg.Language = "hu";
        cfg.Jobs[0].Name = "";
        var file = d.Sub("cfg.json");
        File.WriteAllText(file, ConfigJson.Serialize(cfg));
        var psi = ServiceProcess.Dotnet("bf.dll", "config", "validate", file);
        psi.Environment[BackupForge.Ipc.IpcProtocol.PipeEnvVar] = "bf-missing-" + Guid.NewGuid().ToString("N")[..8]; // no service → offline path
        using var p = System.Diagnostics.Process.Start(psi)!;
        using var err = new MemoryStream();
        var copy = p.StandardError.BaseStream.CopyToAsync(err);
        _ = p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        await copy;
        var bytes = err.ToArray();
        Assert.NotEqual(0, p.ExitCode);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "no BOM");
        Assert.Contains("Kötelező mező", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Service_process_runs_job_via_bf_and_records_chain()
    {
        await using var svc = new ServiceProcess(Cfg());
        await svc.StartAsync();

        var status = await svc.Bf("status");
        Assert.True(status.Code == 0, status.Err);
        Assert.Contains("j1", status.Out, StringComparison.Ordinal);

        var run = await svc.Bf("run", "j1", "--wait");
        Assert.True(run.Code == 0, run.Out + run.Err);

        var h = new HistoryStore(svc.Paths.HistoryDb);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (h.LastRun("j2") is null && DateTime.UtcNow < deadline) await Task.Delay(100);
        Assert.Equal("ok", h.LastRun("j2")?.Result);
        Assert.Equal(h.LastRun("j1")!.ChainRunId, h.LastRun("j2")!.ChainRunId);

        var json = await svc.Bf("status", "--json");
        Assert.Contains("\"status_schema\": 1", json.Out, StringComparison.Ordinal);
        Assert.True(Directory.EnumerateFiles(svc.Paths.LogsDir, "service-*.log").Any());
    }
}
