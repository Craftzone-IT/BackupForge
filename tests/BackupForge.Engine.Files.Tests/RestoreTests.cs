using BackupForge.Core.Abstractions;
using BackupForge.Core.Execution;
using BackupForge.Engine.Files;
using BackupForge.TestKit;

namespace BackupForge.Engine.Files.Tests;

public class RestoreTests
{
    private static async Task<RunOutcome> Restore(EngineHarness h, Dictionary<string, string> p)
    {
        var engine = new FileRestoreEngine(PhysicalFileSystem.Instance, NoOpTargetConnector.Instance);
        var ctx = new JobRunContext
        {
            Config = h.Config, Job = h.Job, Request = new WorkRequest { JobId = h.Job.Id, Kind = RunKinds.Restore, Trigger = RunTriggers.Manual, Parameters = p },
            RunId = 99, Log = new MemoryRunLog(), Progress = new Progress<RunProgress>(), Clock = h.Clock, Secrets = new InMemorySecretStore(), Paths = new DataPaths(h.Dir.Sub("data")),
        };
        return await engine.RunAsync(ctx, CancellationToken.None);
    }

    [Fact]
    public async Task Restore_old_version_folder_and_to_original_with_backup()
    {
        using var h = new EngineHarness();
        h.Write(@"docs\a.txt", "v1");
        h.Write(@"docs\b.txt", "b");
        await h.RunAsync();
        h.Write(@"docs\a.txt", "v2-new", new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc));
        await h.RunAsync();

        var points = FileRestoreEngine.Points(PhysicalFileSystem.Instance, h.Config, h.Job);
        Assert.Equal("current", points[0].Id);
        var stamp = Assert.Single(points.Skip(1)).Id;

        // a single file from the Versions point into a new folder
        var to = h.Dir.Sub("restore1");
        var o = await Restore(h, new() { ["point"] = stamp, ["path"] = @"docs\a.txt", ["target"] = to });
        Assert.Equal(RunResults.Ok, o.Result);
        Assert.Equal("v1", File.ReadAllText(Path.Combine(to, "a.txt")));

        // a folder from the current mirror
        var to2 = h.Dir.Sub("restore2");
        Assert.Equal(RunResults.Ok, (await Restore(h, new() { ["point"] = "current", ["path"] = "docs", ["target"] = to2 })).Result);
        Assert.Equal("v2-new", File.ReadAllText(Path.Combine(to2, "docs", "a.txt")));
        Assert.True(File.Exists(Path.Combine(to2, "docs", "b.txt")));

        // back to the original place: the overwritten file is kept in <source>_RestoreBackup_<stamp>
        var orig = await Restore(h, new() { ["point"] = stamp, ["path"] = @"docs\a.txt", ["to_original"] = "1" });
        Assert.Equal(RunResults.Ok, orig.Result);
        Assert.Equal("v1", File.ReadAllText(Path.Combine(h.Src, "docs", "a.txt")));
        var backupDir = Directory.GetDirectories(h.Dir.Path, "src_RestoreBackup_*").Single();
        Assert.Equal("v2-new", File.ReadAllText(Path.Combine(backupDir, "docs", "a.txt")));
    }

    [Fact]
    public async Task Not_empty_target_and_traversal_are_rejected()
    {
        using var h = new EngineHarness();
        h.Write("a.txt");
        await h.RunAsync();
        var to = h.Dir.Sub("busy");
        Directory.CreateDirectory(to);
        File.WriteAllText(Path.Combine(to, "x"), "x");
        Assert.Equal(RunResults.Failed, (await Restore(h, new() { ["point"] = "current", ["path"] = "", ["target"] = to })).Result);
        Assert.Throws<InvalidOperationException>(() => FileRestoreEngine.Resolve(PhysicalFileSystem.Instance, h.Config, h.Job, "current", @"..\..\etc"));
        Assert.Throws<InvalidOperationException>(() => FileRestoreEngine.Resolve(PhysicalFileSystem.Instance, h.Config, h.Job, "1999-01-01 000000", ""));
    }
}
