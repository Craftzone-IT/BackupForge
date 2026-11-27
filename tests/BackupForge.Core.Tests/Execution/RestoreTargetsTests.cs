using BackupForge.Core.Abstractions;
using BackupForge.Core.Execution;
using BackupForge.TestKit;

namespace BackupForge.Core.Tests.Execution;

/// <summary>CR-002 A5.: two restores in the same second get two folders.</summary>
public class RestoreTargetsTests
{
    [Fact]
    public void Same_second_gets_a_suffix_even_before_the_first_folder_exists()
    {
        using var d = new TempDir();
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 25, 22, 40, 5, TimeSpan.Zero), TimeZoneInfo.Utc);
        var a = RestoreTargets.NewRestoreFolder(d.Path, clock.LocalNow(), Directory.Exists);
        var b = RestoreTargets.NewRestoreFolder(d.Path, clock.LocalNow(), Directory.Exists); // a is only queued, not created yet
        Assert.Equal(Path.Combine(d.Path, "Restore_2026-11-25 224005"), a);
        Assert.Equal(Path.Combine(d.Path, "Restore_2026-11-25 224005 (2)"), b);
        Directory.CreateDirectory(Path.Combine(d.Path, "Restore_2026-11-25 224005 (3)")); // made by hand / an earlier service run
        Assert.Equal(Path.Combine(d.Path, "Restore_2026-11-25 224005 (4)"), RestoreTargets.NewRestoreFolder(d.Path, clock.LocalNow(), Directory.Exists));
    }

    [Fact]
    public void Unique_only_suffixes_on_collision()
    {
        using var d = new TempDir();
        Assert.Equal(Path.Combine(d.Path, "Docs_RestoreBackup_x"), RestoreTargets.Unique(d.Path, "Docs_RestoreBackup_x", Directory.Exists));
        Directory.CreateDirectory(Path.Combine(d.Path, "Docs_RestoreBackup_x"));
        Assert.Equal(Path.Combine(d.Path, "Docs_RestoreBackup_x (2)"), RestoreTargets.Unique(d.Path, "Docs_RestoreBackup_x", Directory.Exists));
    }
}
