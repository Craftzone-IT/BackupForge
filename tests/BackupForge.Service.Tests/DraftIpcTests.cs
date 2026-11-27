using System.Text.Json.Nodes;
using BackupForge.Core.Abstractions;
using BackupForge.Core.Config;
using BackupForge.Engine.Files;
using BackupForge.Ipc;
using BackupForge.TestKit;

namespace BackupForge.Service.Tests;

/// <summary>CR-002 B4.: what the job wizard calls before saving — nothing may be written or stored.</summary>
public class DraftIpcTests
{
    private static (BackupConfig Cfg, string Src, string Target) Setup(TempDir d)
    {
        var src = d.Sub("src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "a.txt"), "alpha");
        var target = d.Sub("target");
        Directory.CreateDirectory(target);
        var cfg = TestConfigs.Jobs(1);
        cfg.Targets[0].Path = target;
        cfg.Jobs[0].Sources[0].Path = src;
        cfg.Jobs[0].Files!.Vss = VssModes.Never;
        return (cfg, src, target);
    }

    private static JsonNode JobNode(JobConfig j) => JsonNode.Parse(ConfigJson.Serialize(new BackupConfig { InstanceName = "x", Jobs = [j] }))!["jobs"]![0]!.DeepClone();
    private static JsonNode TargetNode(TargetConfig t) => JsonNode.Parse(ConfigJson.Serialize(new BackupConfig { InstanceName = "x", Targets = [t] }))!["targets"]![0]!.DeepClone();

    [Fact]
    public async Task Draft_dry_run_plans_a_new_job_with_a_new_target_and_writes_nothing()
    {
        using var d = new TempDir();
        var (cfg, src, _) = Setup(d);
        await using var h = await new RuntimeHarness(cfg).StartAsync([new FileJobEngine(PhysicalFileSystem.Instance, NoOpTargetConnector.Instance, NoSnapshotProvider.Instance)]);
        var newTarget = d.Sub("nas2");
        Directory.CreateDirectory(newTarget);
        var job = new JobConfig
        {
            Id = "docs", Name = "Docs → NAS2", Type = JobTypes.Files, Sources = [new SourceConfig { Path = src }],
            Target = new JobTargetRef { TargetId = "nas2", Subdir = "Docs" }, Files = new FilesOptions { Vss = VssModes.Never },
        };
        await using var c = await h.Connect();
        var r = await c.CallAsync(IpcOps.JobDryRun, new JsonObject { ["job"] = JobNode(job), ["targets"] = new JsonArray(TargetNode(new TargetConfig { Id = "nas2", Path = newTarget })) });
        Assert.Equal("ok", r!["result"]!.ToString());
        Assert.True(r["draft"]!.GetValue<bool>());
        Assert.Contains(r["log"]!.AsArray(), l => l!.ToString().Contains("plan:", StringComparison.Ordinal) && l.ToString().Contains("a.txt", StringComparison.Ordinal));
        Assert.Empty(Directory.EnumerateFileSystemEntries(newTarget));        // nothing copied
        Assert.Null(h.Runtime.ConfigStore.Current.FindJob("docs"));           // nothing saved
        Assert.Null(h.Runtime.ConfigStore.Current.FindTarget("nas2"));
        Assert.Null(h.Runtime.History.LastRun("docs"));                       // no history
    }

    [Fact]
    public async Task Invalid_draft_returns_field_errors()
    {
        using var d = new TempDir();
        var (cfg, src, target) = Setup(d);
        await using var h = await new RuntimeHarness(cfg).StartAsync();
        // a root mirror over j1's mirror (CR-001 overlap) and a missing target
        var bad = new JobConfig { Id = "root", Name = "Root", Type = JobTypes.Files, Sources = [new SourceConfig { Path = src }],
            Target = new JobTargetRef { TargetId = "t", Subdir = "" }, Files = new FilesOptions { Versioning = new VersioningOptions { Folder = d.Sub("v") } } };
        await using var c = await h.Connect();
        var ex = await Assert.ThrowsAsync<IpcException>(() => c.CallAsync(IpcOps.JobDryRun, new JsonObject { ["job"] = JobNode(bad) }));
        Assert.Equal(IpcErrorCodes.Invalid, ex.Code);
        Assert.Contains(ex.Details!.AsArray(), e => e!["code"]!.ToString() == "path.overlap_job" && e["path"]!.ToString() == "jobs[1].target");
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }

    [Fact]
    public async Task Path_check_and_draft_target_test()
    {
        using var d = new TempDir();
        var (cfg, src, _) = Setup(d);
        await using var h = await new RuntimeHarness(cfg).StartAsync();
        await using var c = await h.Connect();
        var ok = await c.CallAsync(IpcOps.PathCheck, new JsonObject { ["path"] = src });
        Assert.True(ok!["exists"]!.GetValue<bool>());
        Assert.True(ok["listable"]!.GetValue<bool>());
        var missing = await c.CallAsync(IpcOps.PathCheck, new JsonObject { ["path"] = d.Sub("nope") });
        Assert.False(missing!["exists"]!.GetValue<bool>());

        var nas = d.Sub("nas3");
        Directory.CreateDirectory(nas);
        File.WriteAllText(Path.Combine(nas, "keep.txt"), "x");
        var t = await c.CallAsync(IpcOps.TargetTest, new JsonObject { ["target"] = TargetNode(new TargetConfig { Id = "nas3", Path = nas }) });
        Assert.True(t!["online"]!.GetValue<bool>());
        Assert.True(t["writable"]!.GetValue<bool>());
        Assert.Equal(["keep.txt"], Directory.EnumerateFileSystemEntries(nas).Select(Path.GetFileName)); // the probe file is gone again
        Assert.Null(h.Runtime.TargetProbe!.Get(new TargetConfig { Id = "nas3", Path = nas }));            // a draft is not cached
    }
}
