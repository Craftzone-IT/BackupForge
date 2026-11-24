using BackupForge.Core.Config;
using BackupForge.Core.Execution;
using BackupForge.Core.Status;
using BackupForge.Engine.Files;
using BackupForge.TestKit;

namespace BackupForge.Engine.Files.Tests;

public class FileEngineTests
{
    [Fact]
    public async Task First_run_copies_everything_second_run_nothing()
    {
        using var h = new EngineHarness();
        h.Write("a.txt", "aaa");
        h.Write(@"sub\b.txt", "bb");
        Directory.CreateDirectory(Path.Combine(h.Src, "empty"));
        var o = await h.RunAsync();
        Assert.Equal(RunResults.Ok, o.Result);
        Assert.Equal(2, o.FilesCopied);
        Assert.Equal(5, o.BytesTransferred);
        Assert.Equal("aaa", File.ReadAllText(h.InMirror("a.txt")));
        Assert.True(Directory.Exists(h.InMirror("empty")));
        Assert.Equal(new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(h.InMirror(@"sub\b.txt")));
        Assert.StartsWith("2 copied, 0 versioned, 0 deleted", o.Summary, StringComparison.Ordinal);

        var again = await h.RunAsync();
        Assert.Equal(0, again.FilesCopied);
        Assert.Equal(RunResults.Ok, again.Result);
        Assert.Empty(h.VersionFolders());
    }

    [Fact]
    public async Task Modified_file_is_updated_and_old_version_kept()
    {
        using var h = new EngineHarness();
        h.Write(@"sub\a.txt", "v1");
        await h.RunAsync();
        h.Write(@"sub\a.txt", "v2-longer", new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc));
        var o = await h.RunAsync();
        Assert.Equal(1, o.FilesCopied);
        Assert.Equal(1, o.FilesVersioned);
        Assert.Equal("v2-longer", File.ReadAllText(h.InMirror(@"sub\a.txt")));
        var stamp = Assert.Single(h.VersionFolders());
        Assert.Equal("2026-11-24 101000", stamp);
        Assert.Equal("v1", File.ReadAllText(Path.Combine(h.Versions, stamp, "sub", "a.txt")));
    }

    [Fact]
    public async Task Mirror_moves_deleted_files_to_versions_update_mode_keeps_them()
    {
        using var h = new EngineHarness();
        for (var i = 0; i < 10; i++) h.Write($"f{i}.txt");
        h.Write(@"old\x.txt");
        await h.RunAsync();
        File.Delete(Path.Combine(h.Src, "f0.txt"));
        Directory.Delete(Path.Combine(h.Src, "old"), true);
        var o = await h.RunAsync();
        Assert.Equal(RunResults.Ok, o.Result);
        Assert.Equal(2, o.FilesDeleted);
        Assert.False(File.Exists(h.InMirror("f0.txt")));
        Assert.False(Directory.Exists(h.InMirror("old")));
        var stamp = Assert.Single(h.VersionFolders());
        Assert.True(File.Exists(Path.Combine(h.Versions, stamp, "old", "x.txt")));

        using var u = new EngineHarness(FilesModes.Update);
        u.Write("keep.txt");
        u.Write("other.txt");
        await u.RunAsync();
        File.Delete(Path.Combine(u.Src, "keep.txt"));
        var uo = await u.RunAsync();
        Assert.Equal(0, uo.FilesDeleted);
        Assert.True(File.Exists(u.InMirror("keep.txt")));
    }

    [Fact]
    public async Task Without_versioning_deleted_files_are_removed()
    {
        using var h = new EngineHarness();
        h.Job.Files!.Versioning.Enabled = false;
        h.Write("a.txt");
        h.Write("b.txt");
        await h.RunAsync();
        File.Delete(Path.Combine(h.Src, "a.txt"));
        h.Write("b.txt", "changed!", new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc));
        var o = await h.RunAsync();
        Assert.Equal(1, o.FilesDeleted);
        Assert.Equal(0, o.FilesVersioned);
        Assert.False(File.Exists(h.InMirror("a.txt")));
        Assert.False(Directory.Exists(h.Versions));
    }

    [Fact]
    public async Task Delete_guard_blocks_mass_delete_until_approved()
    {
        using var h = new EngineHarness();
        for (var i = 0; i < 100; i++) h.Write($"f{i:000}.txt");
        await h.RunAsync();
        for (var i = 0; i < 60; i++) File.Delete(Path.Combine(h.Src, $"f{i:000}.txt"));
        h.Write("new.txt");

        var blocked = await h.RunAsync();
        Assert.Equal(RunResults.Failed, blocked.Result);
        Assert.True(blocked.DeleteGuardTripped);
        Assert.StartsWith("delete guard: 60 files (60%)", blocked.Summary, StringComparison.Ordinal);
        Assert.Equal(101, Directory.GetFiles(h.Mirror).Length); // nothing deleted, the new file copied
        Assert.True(File.Exists(h.InMirror("f000.txt")));
        Assert.True(File.Exists(h.InMirror("new.txt"))); // copies still happen
        Assert.Empty(h.VersionFolders());
        Assert.NotNull(h.State.GetValue(DeleteGuardState.TrippedKey("docs")));

        h.State.SetValue(DeleteGuardState.ApprovedKey("docs"), "yes");
        var approved = await h.RunAsync();
        Assert.Equal(RunResults.Ok, approved.Result);
        Assert.Equal(60, approved.FilesDeleted);
        Assert.Null(h.State.GetValue(DeleteGuardState.ApprovedKey("docs"))); // one-time
        Assert.Null(h.State.GetValue(DeleteGuardState.TrippedKey("docs")));
    }

    [Fact]
    public async Task Delete_guard_allow_flag_and_thresholds()
    {
        using var h = new EngineHarness();
        for (var i = 0; i < 100; i++) h.Write($"f{i:000}.txt");
        await h.RunAsync();
        for (var i = 0; i < 30; i++) File.Delete(Path.Combine(h.Src, $"f{i:000}.txt"));
        var o = await h.RunAsync(new RunOptions { AllowMassDelete = true });
        Assert.Equal(RunResults.Ok, o.Result);
        Assert.Equal(30, o.FilesDeleted);

        // below delete_guard_min_files (50) nothing trips even at a high percentage
        using var small = new EngineHarness();
        for (var i = 0; i < 10; i++) small.Write($"f{i}.txt");
        await small.RunAsync();
        for (var i = 0; i < 9; i++) File.Delete(Path.Combine(small.Src, $"f{i}.txt"));
        Assert.Equal(RunResults.Ok, (await small.RunAsync()).Result);
    }

    [Fact]
    public async Task Empty_or_missing_source_fails_and_touches_nothing()
    {
        using var h = new EngineHarness();
        h.Write("a.txt");
        await h.RunAsync();
        File.Delete(Path.Combine(h.Src, "a.txt"));
        var empty = await h.RunAsync();
        Assert.Equal(RunResults.Failed, empty.Result);
        Assert.Contains("source is empty", empty.Summary, StringComparison.Ordinal);
        Assert.True(File.Exists(h.InMirror("a.txt")));

        Directory.Delete(h.Src);
        var missing = await h.RunAsync();
        Assert.Equal(RunResults.Failed, missing.Result);
        Assert.Contains("source not reachable", missing.Summary, StringComparison.Ordinal);
        Assert.True(File.Exists(h.InMirror("a.txt")));
    }

    [Fact]
    public async Task Missing_target_drive_fails()
    {
        using var h = new EngineHarness();
        h.Write("a.txt");
        h.Config.Targets[0].Path = Path.Combine(h.Dir.Path, "no-such-drive", "sub");
        var o = await h.RunAsync();
        Assert.Equal(RunResults.Failed, o.Result);
        Assert.Contains("target not reachable", o.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Excludes_apply_on_both_sides()
    {
        using var h = new EngineHarness();
        h.Job.Sources[0].Excludes = ["*.tmp", @"\cache\"];
        h.Write("a.txt");
        h.Write("b.tmp");
        h.Write(@"cache\c.txt");
        h.Write(@"x\cache\d.txt");
        h.Write("~$doc.docx");
        h.Write("Thumbs.db");
        h.Write("left.tmp", root: h.Mirror); // pre-existing excluded file in the mirror
        var o = await h.RunAsync();
        Assert.Equal(1, o.FilesCopied);
        Assert.False(File.Exists(h.InMirror("b.tmp")));
        Assert.False(Directory.Exists(h.InMirror("cache")));
        Assert.False(File.Exists(h.InMirror("~$doc.docx")));
        Assert.True(File.Exists(h.InMirror("left.tmp"))); // excluded → not deleted
    }

    [Theory]
    [InlineData(1, false)]      // within 2 s
    [InlineData(3600, false)]   // exactly 1 h (DST / FAT)
    [InlineData(3601, false)]
    [InlineData(7200, true)]    // 2 h → changed
    [InlineData(10, true)]
    public async Task Time_tolerance(int secondsOff, bool expectCopy)
    {
        using var h = new EngineHarness();
        var t = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        h.Write("a.txt", "same", t);
        await h.RunAsync();
        File.SetLastWriteTimeUtc(h.InMirror("a.txt"), t.AddSeconds(secondsOff));
        var o = await h.RunAsync();
        Assert.Equal(expectCopy ? 1 : 0, o.FilesCopied);
        if (secondsOff == 3600) Assert.Contains(h.Log.Lines, l => l.Contains("exactly 1 hour", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Size_change_with_same_time_is_copied()
    {
        using var h = new EngineHarness();
        h.Write("a.txt", "1");
        await h.RunAsync();
        h.Write("a.txt", "12");
        Assert.Equal(1, (await h.RunAsync()).FilesCopied);
    }

    [Fact]
    public async Task Content_compare_hashes_doubtful_files()
    {
        using var h = new EngineHarness();
        h.Job.Files!.Compare = CompareModes.Content;
        var t = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        h.Write("same.txt", "abc", t);
        h.Write("diff.txt", "abc", t);
        await h.RunAsync();
        File.SetLastWriteTimeUtc(h.InMirror("same.txt"), t.AddMinutes(5));
        File.WriteAllText(h.InMirror("diff.txt"), "xyz");
        File.SetLastWriteTimeUtc(h.InMirror("diff.txt"), t.AddMinutes(5));
        var o = await h.RunAsync();
        Assert.Equal(1, o.FilesCopied);
        Assert.Equal("abc", File.ReadAllText(h.InMirror("diff.txt")));
        Assert.Equal(t, File.GetLastWriteTimeUtc(h.InMirror("same.txt"))); // time fixed, not copied
    }

    [Fact]
    public async Task Case_only_rename_is_a_rename()
    {
        using var h = new EngineHarness { Options = new FileEngineOptions { CaseInsensitive = true, RetryDelays = [] } };
        h.Write("readme.txt", "r");
        await h.RunAsync();
        File.Move(Path.Combine(h.Src, "readme.txt"), Path.Combine(h.Src, "README.txt"));
        var o = await h.RunAsync();
        Assert.Equal(0, o.FilesCopied);
        Assert.Equal(0, o.FilesDeleted);
        Assert.Equal(["README.txt"], Directory.GetFiles(h.Mirror).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Leftover_parts_removed_only_inside_own_mirror()
    {
        using var h = new EngineHarness();
        h.Write("a.txt");
        await h.RunAsync();
        var part = h.Write(@"sub\half.doc.bfpart", "junk", root: h.Mirror);
        var foreign = h.Write("foreign.bfpart", "not ours", root: h.TargetRoot);
        await h.RunAsync();
        Assert.False(File.Exists(part));
        Assert.True(File.Exists(foreign));
    }

    [Fact]
    public async Task Dry_run_changes_nothing_and_lists_the_plan()
    {
        using var h = new EngineHarness();
        h.Write("a.txt");
        h.Write("b.txt");
        var o = await h.RunAsync(new RunOptions { DryRun = true });
        Assert.Equal(RunResults.Ok, o.Result);
        Assert.False(Directory.Exists(h.Mirror));
        Assert.StartsWith("dry-run: 2 to copy", o.Summary, StringComparison.Ordinal);
        Assert.Contains(h.Log.Lines, l => l.Contains("plan: CopyNew a.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Readonly_target_file_is_overwritten()
    {
        using var h = new EngineHarness();
        h.Write("a.txt", "v1");
        await h.RunAsync();
        File.SetAttributes(h.InMirror("a.txt"), FileAttributes.ReadOnly);
        h.Write("a.txt", "v2-new", new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc));
        var o = await h.RunAsync();
        Assert.Equal(RunResults.Ok, o.Result);
        Assert.Equal("v2-new", File.ReadAllText(h.InMirror("a.txt")));
    }

    [Fact]
    public async Task Locked_file_is_retried_then_succeeds()
    {
        using var h = new EngineHarness();
        var faulty = new FaultyFileSystem(BackupForge.Core.Abstractions.PhysicalFileSystem.Instance);
        h.Fs = faulty;
        var p = h.Write("locked.txt");
        faulty.FailOpen(p, 2);
        var o = await h.RunAsync();
        Assert.Equal(RunResults.Ok, o.Result);
        Assert.Equal(1, o.FilesCopied);
        Assert.Equal(2, h.Log.Lines.Count(l => l.Contains("retry", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Permanently_locked_file_gives_warning_or_failed_by_ratio()
    {
        using var h = new EngineHarness();
        var faulty = new FaultyFileSystem(BackupForge.Core.Abstractions.PhysicalFileSystem.Instance);
        h.Fs = faulty;
        for (var i = 0; i < 200; i++) h.Write($"f{i:000}.txt");
        faulty.FailOpen(Path.Combine(h.Src, "f007.txt"), int.MaxValue);
        var o = await h.RunAsync();
        Assert.Equal(RunResults.Warning, o.Result); // 1 of 200 = 0.5 % < 1 %
        Assert.Equal(199, o.FilesCopied);
        Assert.Equal(1, o.FilesSkipped);
        Assert.Contains(o.Items, i => i.Path == "f007.txt");

        using var h2 = new EngineHarness();
        var faulty2 = new FaultyFileSystem(BackupForge.Core.Abstractions.PhysicalFileSystem.Instance);
        h2.Fs = faulty2;
        for (var i = 0; i < 10; i++) h2.Write($"f{i}.txt");
        faulty2.FailOpen(Path.Combine(h2.Src, "f1.txt"), int.MaxValue);
        Assert.Equal(RunResults.Failed, (await h2.RunAsync()).Result); // 10 % ≥ 1 %
    }

    [Fact]
    public async Task Lost_target_aborts_run_as_failed()
    {
        using var h = new EngineHarness();
        h.Job.Files!.Parallelism = 1;
        var faulty = new FaultyFileSystem(BackupForge.Core.Abstractions.PhysicalFileSystem.Instance) { LostDirectory = Path.Combine(h.TargetRoot, "Docs"), LoseDirectoryAfterCreates = 3 };
        h.Fs = faulty;
        for (var i = 0; i < 20; i++) h.Write($"f{i:00}.txt");
        var o = await h.RunAsync();
        Assert.Equal(RunResults.Failed, o.Result);
        Assert.Contains("target not reachable", o.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Several_sources_go_to_subfolders()
    {
        using var h = new EngineHarness();
        var second = h.Dir.Sub("Other");
        Directory.CreateDirectory(second);
        h.Write("a.txt", root: h.Src);
        h.Write("b.txt", root: second);
        h.Job.Sources.Add(new SourceConfig { Path = second });
        var o = await h.RunAsync();
        Assert.Equal(2, o.FilesCopied);
        Assert.True(File.Exists(Path.Combine(h.Mirror, "src", "a.txt")));
        Assert.True(File.Exists(Path.Combine(h.Mirror, "Other", "b.txt")));
    }

    [Fact]
    public async Task Symlinks_are_not_followed()
    {
        using var h = new EngineHarness();
        var outside = h.Dir.Sub("outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "s");
        h.Write("a.txt");
        try { Directory.CreateSymbolicLink(Path.Combine(h.Src, "link"), outside); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; } // no symlink permission (Windows without dev mode)
        var o = await h.RunAsync();
        Assert.Equal(1, o.FilesCopied);
        Assert.False(Directory.Exists(h.InMirror("link")));
        Assert.Contains(h.Log.Lines, l => l.Contains("reparse point not followed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ten_thousand_small_files()
    {
        using var h = new EngineHarness();
        for (var d = 0; d < 100; d++)
        {
            var dir = Path.Combine(h.Src, $"d{d:000}");
            Directory.CreateDirectory(dir);
            for (var f = 0; f < 100; f++) File.WriteAllText(Path.Combine(dir, $"f{f:000}.txt"), "x");
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var o = await h.RunAsync();
        Assert.Equal(10_000, o.FilesCopied);
        var again = await h.RunAsync();
        Assert.Equal(0, again.FilesCopied);
        Assert.True(sw.Elapsed < TimeSpan.FromMinutes(10), sw.Elapsed.ToString()); // sanity bound; the hosted Windows runner needs ~2 min
    }

    [Fact]
    public async Task Staging_source_resolves_to_sql_staging_folder()
    {
        using var h = new EngineHarness();
        var staging = Path.Combine(h.Dir.Sub("data"), "staging", "sql-main");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "erp_20261124.bak"), "bak");
        h.Config.Jobs.Add(new JobConfig { Id = "sql-main", Name = "sql", Type = JobTypes.Sql, Sql = new SqlJobOptions { Instance = "." } });
        h.Job.Sources[0].Path = "@staging:sql-main";
        var o = await h.RunAsync();
        Assert.Equal(1, o.FilesCopied);
        Assert.True(File.Exists(h.InMirror("erp_20261124.bak")));
    }
}
