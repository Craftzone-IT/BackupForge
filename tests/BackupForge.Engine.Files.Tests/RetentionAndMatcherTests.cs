using BackupForge.Core.Abstractions;
using BackupForge.Engine.Files;
using BackupForge.TestKit;

namespace BackupForge.Engine.Files.Tests;

public class VersionsRetentionTests
{
    [Fact]
    public void Deletes_only_old_stamp_folders_inside_versions()
    {
        using var d = new TempDir();
        var v = d.Sub("Docs_Versions");
        Directory.CreateDirectory(Path.Combine(v, "2026-10-01 061500", "a"));
        File.WriteAllText(Path.Combine(v, "2026-10-01 061500", "a", "x.txt"), "old");
        Directory.CreateDirectory(Path.Combine(v, "2026-11-20 061500"));
        Directory.CreateDirectory(Path.Combine(v, "2026-10-01 0615")); // not the pattern
        Directory.CreateDirectory(Path.Combine(v, "keep me"));
        File.WriteAllText(Path.Combine(v, "notes.txt"), "file, not folder");
        var sibling = d.Sub("Docs");
        Directory.CreateDirectory(Path.Combine(sibling, "2026-01-01 000000")); // stamp-like folder OUTSIDE versions

        var r = VersionsRetention.Apply(PhysicalFileSystem.Instance, v, 21, null, new DateTime(2026, 11, 24, 10, 0, 0), CancellationToken.None);
        Assert.Equal(1, r.FoldersDeleted);
        Assert.False(Directory.Exists(Path.Combine(v, "2026-10-01 061500")));
        Assert.True(Directory.Exists(Path.Combine(v, "2026-11-20 061500")));
        Assert.True(Directory.Exists(Path.Combine(v, "2026-10-01 0615")));
        Assert.True(Directory.Exists(Path.Combine(v, "keep me")));
        Assert.True(File.Exists(Path.Combine(v, "notes.txt")));
        Assert.True(Directory.Exists(Path.Combine(sibling, "2026-01-01 000000")));
    }

    [Fact]
    public void Missing_versions_folder_is_fine() =>
        Assert.Equal(0, VersionsRetention.Apply(PhysicalFileSystem.Instance, "/nonexistent/x_Versions", 21, null, DateTime.Now, CancellationToken.None).FoldersDeleted);

    [Fact]
    public void Max_versions_per_file_keeps_newest()
    {
        using var d = new TempDir();
        var v = d.Sub("V");
        foreach (var (stamp, content) in new[] { ("2026-11-20 000000", "1"), ("2026-11-21 000000", "2"), ("2026-11-22 000000", "3") })
        {
            Directory.CreateDirectory(Path.Combine(v, stamp, "s"));
            File.WriteAllText(Path.Combine(v, stamp, "s", "a.txt"), content);
        }
        File.WriteAllText(Path.Combine(v, "2026-11-20 000000", "only.txt"), "o");
        var r = VersionsRetention.Apply(PhysicalFileSystem.Instance, v, 365, 2, new DateTime(2026, 11, 24), CancellationToken.None);
        Assert.Equal(1, r.FilesDeleted);
        Assert.False(File.Exists(Path.Combine(v, "2026-11-20 000000", "s", "a.txt")));
        Assert.False(Directory.Exists(Path.Combine(v, "2026-11-20 000000", "s")));
        Assert.True(File.Exists(Path.Combine(v, "2026-11-20 000000", "only.txt")));
        Assert.True(File.Exists(Path.Combine(v, "2026-11-22 000000", "s", "a.txt")));
    }
}

public class ExcludeMatcherTests
{
    [Theory]
    [InlineData("*.tmp", "a.tmp", false, true)]
    [InlineData("*.tmp", @"x\y\a.TMP", false, true)]
    [InlineData("*.tmp", "a.tmpx", false, false)]
    [InlineData(@"\$RECYCLE.BIN\", "$RECYCLE.BIN", true, true)]
    [InlineData(@"\$RECYCLE.BIN\", @"d\$Recycle.Bin", true, true)]
    [InlineData(@"\$RECYCLE.BIN\", "$RECYCLE.BIN", false, false)]
    [InlineData(@"\Projects\old\", @"Projects\old", true, true)]
    [InlineData(@"\Projects\old\", @"x\Projects\old", true, false)]
    [InlineData(@"logs\*.log", @"logs\a.log", false, true)]
    [InlineData(@"logs\*.log", @"x\logs\a.log", false, false)]
    [InlineData(@"**\bin\*.dll", @"a\b\bin\c.dll", false, true)]
    [InlineData(@"**\bin\*.dll", @"bin\c.dll", false, true)]
    [InlineData("~$*", "~$Report.docx", false, true)]
    [InlineData("Thumbs.db", @"p\thumbs.db", false, true)]
    [InlineData("?.txt", "a.txt", false, true)]
    [InlineData("?.txt", "ab.txt", false, false)]
    public void Patterns(string pattern, string rel, bool isDir, bool excluded) =>
        Assert.Equal(excluded, new ExcludeMatcher([pattern]).IsExcluded(rel, isDir));

    [Fact]
    public void Defaults_cover_system_files() =>
        Assert.True(ExcludeMatcher.For([], true).IsExcluded("pagefile.sys", false) && ExcludeMatcher.For([], true).IsExcluded("System Volume Information", true));
}
