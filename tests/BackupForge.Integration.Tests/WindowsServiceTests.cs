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
        var started = DateTime.UtcNow;
        using var dir = new TempDir();
        var paths = new DataPaths(Path.Combine(dir.Path, "data"));
        paths.EnsureCreated();
        var src = Path.Combine(dir.Path, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "doc.txt"), "hello");
        var cfg = TestConfigs.Jobs(1);
        cfg.Targets[0].Path = Path.Combine(dir.Path, "target");
        cfg.Jobs[0].Sources[0].Path = src;
        cfg.Jobs[0].Files!.Vss = VssModes.Never; // the service path is tested here; VSS has its own tests (one snapshot set at a time)
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
            // CR-002 A7.: no ServiceBase.AutoLog entries ("Service started successfully", event id 0) in the Application log
            if (OperatingSystem.IsWindows()) Assert.Equal(0, EventId0Since(started));
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

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static int EventId0Since(DateTime utc)
    {
        var ms = (long)(DateTime.UtcNow - utc).TotalMilliseconds + 1000;
        var q = new System.Diagnostics.Eventing.Reader.EventLogQuery("Application", System.Diagnostics.Eventing.Reader.PathType.LogName,
            $"*[System[Provider[@Name='BackupForge'] and (EventID=0) and TimeCreated[timediff(@SystemTime) <= {ms}]]]");
        using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(q);
        var n = 0;
        for (var e = reader.ReadEvent(); e is not null; e = reader.ReadEvent()) { n++; e.Dispose(); }
        return n;
    }

    private static (int Code, string Output) Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, o);
    }

    /// <summary>
    /// CR-003 2. (the T3 scenario on the runner): the installed service (LocalSystem) with an SMB target that has its own
    /// user, ≥ 3 minutes with the minutely status, 3 runs, restore.versions / restore.browse in between. Every 10 s the SMB
    /// client connections and the server sessions are listed: never one with the computer account / SYSTEM, none left at the end.
    /// </summary>
    [WindowsAdminFact]
    public async Task Installed_service_with_smb_target_never_connects_with_the_computer_account()
    {
        var name = "BackupForgeIT" + Guid.NewGuid().ToString("N")[..6];
        var user = "bfit" + Guid.NewGuid().ToString("N")[..6];
        const string password = "Test-password-123!";
        var share = "bfit" + Guid.NewGuid().ToString("N")[..6];
        using var dir = new TempDir();
        var shareDir = Path.Combine(dir.Path, "share");
        Directory.CreateDirectory(Path.Combine(shareDir, "backup"));
        var src = Path.Combine(dir.Path, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "doc.txt"), "v0");
        var paths = new DataPaths(Path.Combine(dir.Path, "data"));
        paths.EnsureCreated();
        var pipe = "bf-svc-" + Guid.NewGuid().ToString("N")[..8];
        var exe = Path.Combine(ServiceProcess.BinDir, "BackupForge.Service.exe");
        var seen = new List<string>();
        using var stopPolling = new CancellationTokenSource();
        try
        {
            Assert.Equal(0, Run("net", "user", user, password, "/add", "/y").Code);
            Run("icacls", shareDir, "/grant", $"{Environment.MachineName}\\{user}:(OI)(CI)M");
            Assert.Equal(0, Run("powershell", "-NoProfile", "-Command", $"New-SmbShare -Name {share} -Path '{shareDir}' -FullAccess '{Environment.MachineName}\\{user}' | Out-Null").Code);
            var cfg = TestConfigs.Jobs(1);
            cfg.Targets = [new TargetConfig { Id = "nas", Type = TargetTypes.Smb, Path = $@"\\localhost\{share}\backup", Username = $"{Environment.MachineName}\\{user}", PasswordRef = "target:nas" }];
            cfg.Jobs[0].Sources[0].Path = src;
            cfg.Jobs[0].Target = new JobTargetRef { TargetId = "nas", Subdir = "Docs" };
            cfg.Jobs[0].Files!.Vss = VssModes.Never;
            AtomicFile.Write(paths.ConfigFile, ConfigJson.ToUtf8(cfg));
            if (OperatingSystem.IsWindows()) new BackupForge.Windows.DpapiSecretStore(paths.SecretsFile).Set("target:nas", password);

            var create = Sc("create", name, "binPath=", $"\"{exe}\" --data \"{paths.Root}\" --pipe {pipe}", "start=", "demand");
            Assert.Contains("SUCCESS", create, StringComparison.OrdinalIgnoreCase);
            Sc("start", name);
            await ServiceProcess.WaitForPipeAsync(pipe, TimeSpan.FromSeconds(60));

            var poll = Task.Run(async () =>
            {
                while (!stopPolling.IsCancellationRequested)
                {
                    var o = Run("powershell", "-NoProfile", "-Command",
                        "Get-SmbConnection -ErrorAction SilentlyContinue | ForEach-Object { 'C ' + $_.ServerName + '\\' + $_.ShareName + ' cred=' + $_.Credential + ' session=' + $_.UserName }; " +
                        $"Get-SmbSession -ErrorAction SilentlyContinue | ForEach-Object {{ 'S ' + $_.ClientUserName }}").Output;
                    lock (seen) seen.AddRange(o.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    try { await Task.Delay(TimeSpan.FromSeconds(10), stopPolling.Token); } catch (OperationCanceledException) { }
                }
            });

            var started = DateTime.UtcNow;
            await using (var c = await IpcClient.ConnectAsync(pipe))
            {
                for (var i = 1; i <= 3; i++)
                {
                    File.WriteAllText(Path.Combine(src, "doc.txt"), "version " + i);
                    var r = await c.CallAsync(IpcOps.JobRun, new System.Text.Json.Nodes.JsonObject { ["job_id"] = "j1", ["wait"] = true }).WaitAsync(TimeSpan.FromMinutes(2));
                    Assert.True(r!["result"]!.ToString() == RunResults.Ok, $"run {i}: {r["result"]} {r["summary"]}");
                    await c.CallAsync(IpcOps.RestoreVersions, new System.Text.Json.Nodes.JsonObject { ["job_id"] = "j1" });
                    await c.CallAsync(IpcOps.RestoreBrowse, new System.Text.Json.Nodes.JsonObject { ["job_id"] = "j1", ["point"] = "current", ["path"] = "" });
                    var wait = started + TimeSpan.FromSeconds(65 * i) - DateTime.UtcNow;
                    if (wait > TimeSpan.Zero) await Task.Delay(wait);   // ≥ 3 minutes in all: at least 3 minutely status writes
                }
                var status = await c.CallAsync(IpcOps.StatusGet);
                Assert.Equal(2, status!["jobs"]![0]!["versions"]!["folders"]!.GetValue<int>()); // runs 2 and 3 versioned the old doc.txt
            }
            await Task.Delay(TimeSpan.FromSeconds(12));                // one more poll after the last lease
            stopPolling.Cancel();
            await poll;

            string[] snapshot;
            lock (seen) snapshot = [.. seen];
            var machine = Environment.MachineName + "$";
            // Get-SmbConnection: "session=" is the logon session that owns the connection (always <machine>$ for LocalSystem);
            // "cred=" is the credential the connection was made with — that must be the target's user, never the computer account.
            // Get-SmbSession (server side) shows the client user of every session.
            static string Cred(string l) => l.Contains(" cred=", StringComparison.Ordinal) ? l[(l.IndexOf(" cred=", StringComparison.Ordinal) + 6)..].Split(" session=")[0] : "";
            var ours = snapshot.Where(l => l.StartsWith("C ", StringComparison.Ordinal) && l.Contains(share, StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.True(ours.Count > 0, "the polling never saw the service's connection: " + string.Join(" / ", snapshot.Take(10)));
            Assert.All(ours, l => Assert.Contains(user, Cred(l), StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(snapshot, l => l.StartsWith("S ", StringComparison.Ordinal) && l.Contains(machine, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(snapshot, l => l.StartsWith("C ", StringComparison.Ordinal) && Cred(l).Contains(machine, StringComparison.OrdinalIgnoreCase));
            var leftover = Run("powershell", "-NoProfile", "-Command", $"Get-SmbConnection -ErrorAction SilentlyContinue | Where-Object ShareName -eq '{share}' | Measure-Object | ForEach-Object Count").Output.Trim();
            Assert.True(leftover == "0", "connections left: " + leftover + " | " + string.Join(" / ", snapshot.TakeLast(10)));
            Sc("stop", name);
            for (var i = 0; i < 120 && !Sc("query", name).Contains("STOPPED", StringComparison.OrdinalIgnoreCase); i++) await Task.Delay(500);
            var log = string.Concat(Directory.EnumerateFiles(paths.LogsDir, "service-*.log").Select(f =>
            {
                using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return new StreamReader(fs).ReadToEnd();
            }));
            Assert.DoesNotContain("without a connection lease", log, StringComparison.Ordinal); // the production guard stayed silent
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(log, "BackupForge stopping")); // the nit: once
        }
        finally
        {
            stopPolling.Cancel();
            Sc("stop", name);
            for (var i = 0; i < 60 && !Sc("query", name).Contains("STOPPED", StringComparison.OrdinalIgnoreCase); i++) await Task.Delay(500);
            Sc("delete", name);
            Run("powershell", "-NoProfile", "-Command", $"Remove-SmbShare -Name {share} -Force -ErrorAction SilentlyContinue");
            Run("net", "user", user, "/delete");
        }
    }
}
