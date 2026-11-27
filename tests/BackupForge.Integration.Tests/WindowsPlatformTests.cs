using System.Diagnostics;
using System.Security.AccessControl;
using BackupForge.Core.Abstractions;
using BackupForge.Core.Config;
using BackupForge.Core.Execution;
using BackupForge.Engine.Files;
using BackupForge.TestKit;
using BackupForge.Windows;

namespace BackupForge.Integration.Tests;

[Collection("vss")] // VSS allows one snapshot set at a time per machine: these tests must not overlap
public class WindowsPlatformTests
{
    [WindowsOnlyFact]
    public void Dpapi_secret_store_round_trip_and_acl()
    {
        using var d = new TempDir();
        var path = d.Sub("secrets.dat");
        var s = new DpapiSecretStore(path);
        s.Set("target:nas1", "test-password-123");
        s.Set("smtp", "test-smtp-pw-789");
        Assert.DoesNotContain("test-password-123", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path)), StringComparison.Ordinal);
        var again = new DpapiSecretStore(path);
        Assert.Equal("test-password-123", again.Get("target:nas1"));
        Assert.True(again.Remove("smtp"));
        Assert.False(new DpapiSecretStore(path).Has("smtp"));
        var acl = new FileInfo(path).GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected);
    }

    [WindowsAdminFact]
    public void Vss_snapshot_reads_an_exclusively_locked_file()
    {
        using var d = new TempDir();
        var file = d.Sub("locked.db");
        File.WriteAllText(file, "database content");
        using var locker = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<IOException>(() => File.ReadAllText(file));
        var vss = new VssSnapshotProvider();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        using var session = vss.Create([d.Path], cts.Token);
        var inSnapshot = session.Translate(file);
        Assert.StartsWith(@"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy", inSnapshot, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("database content", File.ReadAllText(inSnapshot));
        Assert.NotEmpty(session.Snapshots);
    }

    [WindowsAdminFact]
    public async Task Bf_diag_vss_without_writers_reads_a_locked_file_from_the_snapshot()
    {
        // CR-002 A1.: the diagnostic command must succeed on the runner (writer-less path) and read the locked file back
        using var d = new TempDir();
        var dir = d.Sub("only");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "locked.db");
        File.WriteAllText(file, "database content");
        using var locker = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var o = new StringWriter();
        var code = await BackupForge.Cli.BfCli.RunAsync(["diag", "vss", dir, "--no-writers"], o, o, TextReader.Null);
        var text = o.ToString();
        Assert.True(code == 0, text);
        Assert.Contains("read 16 byte(s)", text, StringComparison.Ordinal);
        Assert.Contains("(FINISHED)", text, StringComparison.Ordinal);
        Assert.Contains("RESULT: ok", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Environment.MachineName, text, StringComparison.OrdinalIgnoreCase);
    }

    [WindowsAdminFact]
    public async Task Files_job_with_vss_copies_locked_file_without_vss_it_is_skipped()
    {
        using var d = new TempDir();
        var src = d.Sub("src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "free.txt"), "free");
        File.WriteAllText(Path.Combine(src, "locked.db"), "db");
        using var locker = new FileStream(Path.Combine(src, "locked.db"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var withVss = await RunFilesJob(d, src, VssModes.Required, new VssSnapshotProvider());
        var detail = withVss.Summary + " | " + string.Join(" | ", withVss.Items.Select(i => i.Path + ": " + i.Message));
        // CR-001 2.: the writer-less fallback (hosted runners) must show up as a warning; with writers the run is ok
        var fellBack = withVss.Items.Any(i => i.Message.Contains("writers did not respond", StringComparison.Ordinal));
        Assert.True(withVss.Result == (fellBack ? RunResults.Warning : RunResults.Ok), detail);
        if (withVss.Result == RunResults.Warning) Assert.True(fellBack, detail);
        Assert.Equal("db", File.ReadAllText(d.Sub("target", "Docs", "locked.db")));

        Directory.Delete(d.Sub("target"), true);
        var without = await RunFilesJob(d, src, VssModes.Never, NoSnapshotProvider.Instance);
        Assert.Equal(RunResults.Failed, without.Result); // 1 of 2 files (50 %) could not be read
        Assert.Contains(without.Items, i => i.Path == "locked.db");
    }

    [WindowsOnlyFact]
    public async Task Long_paths_over_260_characters()
    {
        using var d = new TempDir();
        var src = d.Sub("src");
        var deep = src;
        while (deep.Length < 300) deep = Path.Combine(deep, "a-rather-long-folder-name-segment");
        Directory.CreateDirectory(deep);
        var file = Path.Combine(deep, "deep-file.txt");
        File.WriteAllText(file, "deep");
        Assert.True(file.Length > 260);
        var o = await RunFilesJob(d, src, VssModes.Never, NoSnapshotProvider.Instance);
        Assert.Equal(RunResults.Ok, o.Result);
        Assert.Equal(1, o.FilesCopied);
        File.WriteAllText(file, "deeper!");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(-5));
        var o2 = await RunFilesJob(d, src, VssModes.Never, NoSnapshotProvider.Instance);
        Assert.Equal(1, o2.FilesVersioned);
    }

    [WindowsOnlyFact]
    public async Task Attributes_are_preserved()
    {
        using var d = new TempDir();
        var src = d.Sub("src");
        Directory.CreateDirectory(src);
        var f = Path.Combine(src, "h.txt");
        File.WriteAllText(f, "h");
        File.SetAttributes(f, FileAttributes.Hidden | FileAttributes.ReadOnly | FileAttributes.Archive);
        var o = await RunFilesJob(d, src, VssModes.Never, NoSnapshotProvider.Instance);
        Assert.Equal(RunResults.Ok, o.Result);
        var a = File.GetAttributes(d.Sub("target", "Docs", "h.txt"));
        Assert.True(a.HasFlag(FileAttributes.Hidden) && a.HasFlag(FileAttributes.ReadOnly));
        File.SetAttributes(f, FileAttributes.Normal);
        File.SetAttributes(d.Sub("target", "Docs", "h.txt"), FileAttributes.Normal);
    }

    [WindowsAdminFact]
    public void Event_log_entries_are_written()
    {
        var sink = new WindowsEventSink();
        var marker = "BackupForge integration test " + Guid.NewGuid();
        sink.Write(Core.Notifications.EventLevel.Warning, Core.Notifications.EventIds.JobWarning, marker);
        using var log = new EventLog(EventLogSink.LogName);
        Assert.Contains(log.Entries.Cast<EventLogEntry>().Reverse().Take(200), e => e.Source == EventLogSink.Source && e.Message.Contains(marker, StringComparison.Ordinal) && e.InstanceId == 2001);
    }

    [WindowsAdminFact]
    public void Smb_target_with_own_credentials()
    {
        var user = "bfit" + Guid.NewGuid().ToString("N")[..6];
        const string password = "Test-password-123!";
        var share = "bfit" + Guid.NewGuid().ToString("N")[..6];
        using var d = new TempDir();
        try
        {
            var created = Run("net", "user", user, password, "/add", "/y");
            Assert.True(created.Code == 0, "net user: " + created.Output);
            Run("icacls", d.Path, "/grant", $"{Environment.MachineName}\\{user}:(OI)(CI)M");
            var shared = Run("powershell", "-NoProfile", "-Command", $"New-SmbShare -Name {share} -Path '{d.Path}' -FullAccess '{Environment.MachineName}\\{user}' | Out-Null");
            Assert.True(shared.Code == 0, "New-SmbShare: " + shared.Output);
            var unc = $@"\\localhost\{share}";
            var secrets = new InMemorySecretStore([new("target:t", password)]);
            var target = new TargetConfig { Id = "t", Type = TargetTypes.Smb, Path = unc + @"\backup", Username = $"{Environment.MachineName}\\{user}", PasswordRef = "target:t" };
            var connector = new WNetConnector();
            using (connector.Connect(target, secrets))
            {
                Directory.CreateDirectory(target.Path);
                File.WriteAllText(Path.Combine(target.Path, "x.txt"), "via smb");
            }
            Assert.Equal("via smb", File.ReadAllText(Path.Combine(d.Path, "backup", "x.txt")));
            var bad = new InMemorySecretStore([new("target:t", "wrong-password")]);
            var ex = Assert.Throws<TargetConnectException>(() => connector.Connect(target, bad).Dispose());
            Assert.NotNull(ex.ErrorCode);
        }
        finally
        {
            Run("powershell", "-NoProfile", "-Command", $"Remove-SmbShare -Name {share} -Force -ErrorAction SilentlyContinue");
            Run("net", "user", user, "/delete");
        }
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

    private static async Task<RunOutcome> RunFilesJob(TempDir d, string src, string vss, ISnapshotProvider provider)
    {
        var target = d.Sub("target");
        Directory.CreateDirectory(target);
        var cfg = new BackupConfig
        {
            InstanceName = "IT", Language = "en",
            Targets = [new TargetConfig { Id = "t", Path = target }],
            Jobs = [new JobConfig
            {
                Id = "docs", Name = "Docs", Type = JobTypes.Files, Sources = [new SourceConfig { Path = src }],
                Target = new JobTargetRef { TargetId = "t", Subdir = "Docs" }, Files = new FilesOptions { Vss = vss },
            }],
        };
        var engine = new FileJobEngine(PhysicalFileSystem.Instance, NoOpTargetConnector.Instance, provider, new FileEngineOptions { RetryDelays = [TimeSpan.Zero] });
        var ctx = new JobRunContext
        {
            Config = cfg, Job = cfg.Jobs[0], Request = new WorkRequest { JobId = "docs", Trigger = RunTriggers.Manual }, RunId = 1,
            Log = new MemoryRunLog(), Progress = new Progress<RunProgress>(), Clock = new ManualClock(DateTimeOffset.UtcNow, TimeZoneInfo.Local),
            Secrets = new InMemorySecretStore(), Paths = new DataPaths(d.Sub("data")),
        };
        await Task.Delay(1100); // distinct Versions stamp per run
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        return await engine.RunAsync(ctx, cts.Token).WaitAsync(TimeSpan.FromMinutes(5));
    }
}
