using System.Diagnostics;
using BackupForge.Core.Config;
using BackupForge.Core.Execution;
using BackupForge.Ipc;
using BackupForge.TestKit;

namespace BackupForge.Integration.Tests;

/// <summary>Installs the real exe as a Windows service (LocalSystem) on the CI runner, runs a job, removes it.</summary>
[Collection("vss")] // VSS allows one snapshot set at a time per machine: these tests must not overlap
public class WindowsServiceTests
{
    private static string Sc(params string[] args)
    {
        var psi = new ProcessStartInfo("sc.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return o;
    }

    [WindowsAdminFact]
    public Task Installed_service_runs_job_and_stops_cleanly() => RunInstalled(testEngine: true);

    [WindowsAdminFact]
    public Task Installed_service_runs_real_files_job_as_localsystem() => RunInstalled(testEngine: false);

    private async Task RunInstalled(bool testEngine)
    {
        var name = "BackupForgeIT" + Guid.NewGuid().ToString("N")[..6];
        using var dir = new TempDir();
        var paths = new DataPaths(Path.Combine(dir.Path, "data"));
        paths.EnsureCreated();
        var src = Path.Combine(dir.Path, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "doc.txt"), "hello");
        var cfg = TestConfigs.Jobs(1);
        cfg.Targets[0].Path = Path.Combine(dir.Path, "target");
        cfg.Jobs[0].Sources[0].Path = src;
        AtomicFile.Write(paths.ConfigFile, ConfigJson.ToUtf8(cfg));
        var pipe = "bf-svc-" + Guid.NewGuid().ToString("N")[..8];
        var exe = Path.Combine(ServiceProcess.BinDir, "BackupForge.Service.exe");
        Assert.True(File.Exists(exe), exe);
        try
        {
            var bin = $"\"{exe}\" --data \"{paths.Root}\" --pipe {pipe}" + (testEngine ? " --test-engine" : "");
            var create = Sc("create", name, "binPath=", bin, "start=", "demand");
            Assert.Contains("SUCCESS", create, StringComparison.OrdinalIgnoreCase);
            var start = Sc("start", name);
            Assert.True(start.Contains("START_PENDING", StringComparison.OrdinalIgnoreCase) || start.Contains("RUNNING", StringComparison.OrdinalIgnoreCase), start);
            await ServiceProcess.WaitForPipeAsync(pipe, TimeSpan.FromSeconds(60));

            await using (var c = await IpcClient.ConnectAsync(pipe))
            {
                var r = await c.CallAsync(IpcOps.JobRun, new System.Text.Json.Nodes.JsonObject { ["job_id"] = "j1", ["wait"] = true }).WaitAsync(TimeSpan.FromMinutes(4));
                Assert.Equal(RunResults.Ok, r!["result"]!.ToString());
            }
            if (!testEngine) Assert.Equal("hello", File.ReadAllText(Path.Combine(dir.Path, "target", "d1", "doc.txt")));

            Sc("stop", name);
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!Sc("query", name).Contains("STOPPED", StringComparison.OrdinalIgnoreCase) && DateTime.UtcNow < deadline) await Task.Delay(500);
            Assert.Contains("STOPPED", Sc("query", name), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("stopped", string.Concat(Directory.EnumerateFiles(paths.LogsDir, "service-*.log").Select(f =>
            {
                using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return new StreamReader(fs).ReadToEnd();
            })), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Sc("stop", name);
            Sc("delete", name);
        }
    }
}
