using BackupForge.Engine.Restic;

namespace BackupForge.Engine.Restic.Tests;

/// <summary>Real restic 0.20.0 outputs (fixtures/, paths replaced with /data).</summary>
public class ResticParserTests
{
    private static string Fx(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);
    private static string[] Lines(string name) => File.ReadAllLines(Fx(name));

    [Fact]
    public void Backup_summary_is_parsed()
    {
        var last = Lines("backup-ok.jsonl").Last();
        Assert.Equal("summary", ResticParser.MessageType(last));
        var s = ResticParser.Parse<ResticBackupSummary>(last)!;
        Assert.Equal(2, s.FilesNew);
        Assert.Equal(12, s.TotalBytesProcessed);
        Assert.StartsWith("c599c9b7", s.SnapshotId, StringComparison.Ordinal);
    }

    [Fact]
    public void Exit_3_has_error_items_and_exit_error()
    {
        var err = Lines("backup-exit3.stderr");
        var e = ResticParser.ParseError(err[0])!;
        Assert.Equal("/data/src/sub/b.txt", e.Item);
        Assert.Contains("permission denied", e.Message, StringComparison.Ordinal);
        var exit = ResticParser.ParseExitError(err[1])!;
        Assert.Equal(3, exit.Code);
        Assert.Equal("summary", ResticParser.MessageType(Lines("backup-exit3.jsonl").Last()));
    }

    [Theory]
    [InlineData("wrong-password.stderr", 12)]
    [InlineData("no-repo.stderr", 10)]
    [InlineData("locked.stderr", 11)]
    public void Fatal_exit_errors(string file, int code) =>
        Assert.Equal(code, ResticParser.ParseExitError(Lines(file)[0])!.Code);

    [Fact]
    public void Snapshots_ls_check_forget_restore_version()
    {
        var snaps = ResticParser.Snapshots(File.ReadAllText(Fx("snapshots.json")));
        Assert.Equal(2, snaps.Count);
        Assert.Equal("SRV01", snaps[0].Hostname);
        Assert.Equal(["bf:docs"], snaps[0].Tags);
        var nodes = Lines("ls.jsonl").Where(l => ResticParser.MessageType(l) == "node").Select(ResticParser.Parse<ResticNode>).ToList();
        Assert.Equal(4, nodes.Count);
        Assert.Contains(nodes, n => n!.Path == "/src/sub/b.txt" && n.Type == "file" && n.Size == 6);
        var check = ResticParser.Parse<ResticCheckSummary>(File.ReadAllText(Fx("check-ok.json")).Trim())!;
        Assert.Equal(0, check.NumErrors);
        var groups = ResticParser.ForgetGroups(File.ReadAllText(Fx("forget.json")));
        Assert.Equal(1, groups.Sum(g => g.Keep!.Count));
        Assert.Equal(1, groups.Sum(g => g.Remove?.Count ?? 0));
        var restore = ResticParser.Parse<ResticRestoreSummary>(Lines("restore.jsonl").Last())!;
        Assert.Equal(4, restore.FilesRestored);
        Assert.Contains("0.20.0", File.ReadAllText(Fx("version.json")), StringComparison.Ordinal);
    }

    [Fact]
    public void Lock_message_host_and_age()
    {
        var msg = ResticParser.ParseExitError(Lines("locked.stderr")[0])!.Message;
        Assert.Equal("SRV01", ResticParser.LockHost(msg));
        Assert.Equal(new TimeSpan(7, 12, 3), ResticParser.LockAge(msg));
        Assert.Equal(TimeSpan.FromMinutes(5), ResticParser.LockAge("lock was created at x (5m0s ago)"));
        Assert.Null(ResticParser.LockAge("no age here"));
    }

    [Fact]
    public void Stale_lock_rule_requires_own_host_and_age()
    {
        var host = Environment.MachineName;
        Assert.True(ResticEngine.IsOurStaleLock($"locked by PID 1 on {host} by x\nlock was created at t (1h0m0s ago)"));
        Assert.False(ResticEngine.IsOurStaleLock($"locked by PID 1 on {host} by x\nlock was created at t (10m0s ago)"));
        Assert.False(ResticEngine.IsOurStaleLock("locked by PID 1 on OTHERHOST by x\nlock was created at t (5h0m0s ago)"));
    }

    [Theory]
    [InlineData(@"\$RECYCLE.BIN\", "$RECYCLE.BIN")]
    [InlineData("*.tmp", "*.tmp")]
    [InlineData(@"\Projects\old\", @"\Projects\old")]
    public void Exclude_patterns(string ours, string restic) => Assert.Equal(restic, ResticEngine.ToResticPattern(ours));

    [Fact]
    public void Pinned_version_is_embedded() => Assert.Equal("0.20.0", ResticBinary.PinnedVersion);
}

public class ResticPathsTests
{
    [Theory]
    [InlineData(@"C:\data\x", "/C/data/x")]
    [InlineData("C:/data/x", "/C/data/x")]
    [InlineData(@"c:\data\x\", "/C/data/x")]
    [InlineData("/C/data/x", "/C/data/x")]
    [InlineData("/home/u/docs/", "/home/u/docs")]
    [InlineData(@"data\x", "/data/x")]
    [InlineData("/", "/")]
    [InlineData(@"C:\", "/C")]
    public void To_snapshot_path(string input, string expected) => Assert.Equal(expected, ResticPaths.ToSnapshotPath(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Empty_is_null(string? input) => Assert.Null(ResticPaths.ToSnapshotPath(input));
}
