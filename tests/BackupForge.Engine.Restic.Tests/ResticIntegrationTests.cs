using BackupForge.Core.Abstractions;
using BackupForge.Core.Config;
using BackupForge.Core.Execution;
using BackupForge.Core.Logging;
using BackupForge.TestKit;

namespace BackupForge.Engine.Restic.Tests;

/// <summary>Real restic (pinned 0.20.0) against a local repository.</summary>
public sealed class ResticIntegrationTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly InMemorySecretStore _secrets = new([new("restic:local", "test-restic-pw-456")]);
    private readonly SecretMasker _masker = new();
    private readonly MemoryKeyValueStore _state = new();
    private readonly BackupConfig _cfg;
    private IRunLog? LastLog;
    private string Src => _dir.Sub("src");
    private string RepoPath => _dir.Sub("repo");

    public ResticIntegrationTests()
    {
        Directory.CreateDirectory(Src);
        File.WriteAllText(Path.Combine(Src, "a.txt"), "alpha");
        Directory.CreateDirectory(Path.Combine(Src, "sub"));
        File.WriteAllText(Path.Combine(Src, "sub", "b.txt"), "beta");
        _cfg = new BackupConfig
        {
            InstanceName = "SRV01", Language = "en",
            ResticRepos = [new ResticRepoConfig
            {
                Id = "local", Location = RepoPath, PasswordRef = "restic:local",
                Retention = new ResticRetention { KeepLast = 1, KeepDaily = null, KeepWeekly = null, KeepMonthly = null, KeepYearly = null },
            }],
            Jobs = [new JobConfig
            {
                Id = "docs", Name = "Docs", Type = JobTypes.Restic, Sources = [new SourceConfig { Path = Src, Excludes = ["*.tmp"] }],
                Restic = new ResticJobOptions { RepoId = "local", Vss = VssModes.Never },
            }],
        };
    }

    public void Dispose() => _dir.Dispose();

    private ResticBinary Bin => new(ResticTestEnv.Binary ?? throw new InvalidOperationException("restic binary required (BF_REQUIRE_RESTIC=1)"), null);
    private ResticClient Client => new(Bin, new SystemProcessRunner(), _dir.Sub("cache"), _masker);

    private async Task<RunOutcome> Run(string kind = RunKinds.Backup, string? jobId = null, CancellationToken ct = default)
    {
        var engine = new ResticEngine(Bin, new SystemProcessRunner(), _masker);
        var id = jobId ?? (kind == RunKinds.Backup ? "docs" : MaintenanceIds.Repo("local"));
        var ctx = new JobRunContext
        {
            Config = _cfg, Job = _cfg.FindJob(id), Request = new WorkRequest { JobId = id, Kind = kind, Trigger = RunTriggers.Manual },
            RunId = 1, Log = LastLog = new MemoryRunLog(), Progress = new Progress<RunProgress>(), Clock = SystemClock.Instance, Secrets = _secrets,
            Paths = new DataPaths(_dir.Sub("data")), State = _state,
        };
        return await engine.RunAsync(ctx, ct);
    }

    [ResticFact]
    public async Task Init_backup_snapshots_ls_restore_forget_check()
    {
        var repo = _cfg.ResticRepos[0];
        Assert.Contains("initialized", await Client.InitAsync(repo, _secrets, CancellationToken.None), StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(Src, "skip.tmp"), "temp");
        var first = await Run();
        Assert.Equal(RunResults.Ok, first.Result);
        Assert.StartsWith("snapshot ", first.Summary, StringComparison.Ordinal);
        Assert.True(first.FilesCopied == 2, first.Summary + " / " + string.Join("; ", ((MemoryRunLog)LastLog!).Lines));
        Assert.NotNull(_state.GetValue(ResticState.LastSnapshotKey("docs")));
        File.WriteAllText(Path.Combine(Src, "a.txt"), "alpha v2");
        Assert.Equal(RunResults.Ok, (await Run()).Result);
        Assert.Equal("2", _state.GetValue(ResticState.SnapshotCountKey("docs")));

        var snaps = await Client.SnapshotsAsync(repo, _secrets, "bf:docs", CancellationToken.None);
        Assert.Equal(2, snaps.Count);
        Assert.All(snaps, s => Assert.Equal(Environment.MachineName, s.Hostname));
        var nodes = await Client.LsAsync(repo, _secrets, snaps[^1].ShortId, null, CancellationToken.None);
        Assert.Contains(nodes, n => n.Name == "b.txt");
        Assert.DoesNotContain(nodes, n => n.Name == "skip.tmp");

        var target = _dir.Sub("restore");
        var r = await Client.RestoreAsync(repo, _secrets, snaps[0].ShortId, null, target, overwrite: false, null, CancellationToken.None);
        Assert.True(r!.FilesRestored >= 2); // restic counts the restored folders too
        var restored = Directory.GetFiles(target, "a.txt", SearchOption.AllDirectories).Single();
        Assert.Equal("alpha", File.ReadAllText(restored)); // first snapshot = old content

        var forget = await Run(RunKinds.ForgetPrune);
        Assert.Equal(RunResults.Ok, forget.Result);
        Assert.Contains("1 snapshot", forget.Summary, StringComparison.Ordinal);
        Assert.Single(await Client.SnapshotsAsync(repo, _secrets, null, CancellationToken.None));

        var check = await Run(RunKinds.Check);
        Assert.Equal(RunResults.Ok, check.Result);
    }

    [ResticFact]
    public async Task Missing_repository_is_never_created_automatically()
    {
        var o = await Run();
        Assert.Equal(RunResults.Failed, o.Result);
        Assert.Contains("does not exist", o.Summary, StringComparison.Ordinal);
        Assert.False(Directory.Exists(RepoPath) && Directory.EnumerateFileSystemEntries(RepoPath).Any());
    }

    [ResticFact]
    public async Task Wrong_password_is_reported_and_secret_never_shown()
    {
        await Client.InitAsync(_cfg.ResticRepos[0], _secrets, CancellationToken.None);
        _secrets.Set("restic:local", "wrong-password-999");
        _masker.Register("wrong-password-999");
        var o = await Run();
        Assert.Equal(RunResults.Failed, o.Result);
        Assert.Contains("wrong repository password", o.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("wrong-password-999", o.Summary, StringComparison.Ordinal);
    }

    [ResticFact]
    public async Task Unreadable_file_gives_warning_exit_3()
    {
        await Client.InitAsync(_cfg.ResticRepos[0], _secrets, CancellationToken.None);
        var locked = Path.Combine(Src, "sub", "b.txt");
        FileStream? hold = null;
        if (OperatingSystem.IsWindows()) hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        else if (Environment.UserName == "root") return; // root reads everything — the CI runner is not root
        else File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            var o = await Run();
            Assert.Equal(RunResults.Warning, o.Result);
            Assert.Contains(o.Items, i => i.Path is not null && i.Path.EndsWith("b.txt", StringComparison.Ordinal));
        }
        finally
        {
            hold?.Dispose();
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [ResticFact]
    public async Task Cancel_kills_restic_and_repository_stays_usable()
    {
        await Client.InitAsync(_cfg.ResticRepos[0], _secrets, CancellationToken.None);
        var big = Path.Combine(Src, "big.bin");
        using (var fs = File.Create(big))
        {
            var buf = new byte[1 << 20];
            var rnd = new Random(1);
            for (var i = 0; i < 300; i++) { rnd.NextBytes(buf); fs.Write(buf); }
        }
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(ct: cts.Token));
        File.Delete(big);
        Assert.Equal(RunResults.Ok, (await Run()).Result);
    }

    [ResticFact]
    public async Task Version_is_the_pinned_one()
    {
        Assert.Equal(ResticBinary.PinnedVersion, await Client.VersionAsync(CancellationToken.None));
    }
}
