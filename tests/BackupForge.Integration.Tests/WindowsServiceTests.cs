using System.Diagnostics;
using BackupForge.Core.Config;
using BackupForge.Core.Execution;
using BackupForge.Ipc;
using BackupForge.TestKit;

namespace BackupForge.Integration.Tests;

/// <summary>Installs the real exe as a Windows service (LocalSystem) on the CI runner, runs a job, removes it.</summary>
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
    public async Task Installed_service_runs_job_and_stops_cleanly()
    {
        var name = "BackupForgeIT" + Guid.NewGuid().ToString("N")[..6];
        using var dir = new TempDir();
        var paths = new DataPaths(dir.Path);
        paths.EnsureCreated();
        AtomicFile.Write(paths.ConfigFile, ConfigJson.ToUtf8(TestConfigs.Jobs(1)));
        var pipe = "bf-svc-" + Guid.NewGuid().ToString("N")[..8];
        var exe = Path.Combine(ServiceProcess.BinDir, "BackupForge.Service.exe");
        Assert.True(File.Exists(exe), exe);
        try
        {
            var bin = $"\"{exe}\" --data \"{paths.Root}\" --pipe {pipe} --test-engine";
            var create = Sc("create", name, "binPath=", bin, "start=", "demand");
            Assert.Contains("SUCCESS", create, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("SUCCESS", Sc("start", name), StringComparison.OrdinalIgnoreCase);
            await ServiceProcess.WaitForPipeAsync(pipe, TimeSpan.FromSeconds(60));

            await using (var c = await IpcClient.ConnectAsync(pipe))
            {
                var r = await c.CallAsync(IpcOps.JobRun, new System.Text.Json.Nodes.JsonObject { ["job_id"] = "j1", ["wait"] = true });
                Assert.Equal(RunResults.Ok, r!["result"]!.ToString());
            }

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
