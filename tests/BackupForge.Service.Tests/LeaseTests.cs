using System.Text.Json.Nodes;
using BackupForge.Core.Abstractions;
using BackupForge.Core.Config;
using BackupForge.Core.Execution;
using BackupForge.Engine.Files;
using BackupForge.Ipc;
using BackupForge.TestKit;

namespace BackupForge.Service.Tests;

/// <summary>
/// CR-003 2.: with a credentialed SMB target the service touches the target ONLY inside a lease — the minutely status
/// (Versions info), restore.versions / restore.browse and path.check included. On Linux the share is a mapped temp folder.
/// </summary>
public class LeaseTests
{
    /// <summary>Counts live leases like the SMB connection manager.</summary>
    private sealed class CountingConnector : ITargetConnector
    {
        public int Active;
        public int Total;
        public IDisposable Connect(TargetConfig target, ISecretStore secrets)
        {
            if (target.Type != TargetTypes.Smb || string.IsNullOrEmpty(target.Username)) return NullDisposable.Instance;
            Interlocked.Increment(ref Active);
            Interlocked.Increment(ref Total);
            return new Lease(this);
        }
        private sealed class Lease(CountingConnector c) : IDisposable { private int _d; public void Dispose() { if (Interlocked.Exchange(ref _d, 1) == 0) Interlocked.Decrement(ref c.Active); } }
    }

    /// <summary>\\nas01\backup\… → a temp folder; everything else unchanged.</summary>
    private sealed class MappedFs(string root) : IFileSystem
    {
        private const string Share = @"\\nas01\backup";
        private static readonly IFileSystem P = PhysicalFileSystem.Instance;
        public string Map(string path) => path.StartsWith(Share, StringComparison.OrdinalIgnoreCase)
            ? root + path[Share.Length..].Replace('\\', '/') : path;
        private string Unmap(string path) => path.StartsWith(root, StringComparison.Ordinal) ? Share + path[root.Length..].Replace('/', '\\') : path;
        private FsEntry Back(FsEntry e) => e with { FullPath = Unmap(e.FullPath) };
        public char Separator => P.Separator;
        public bool IsCaseSensitive => P.IsCaseSensitive;
        public bool FileExists(string path) => P.FileExists(Map(path));
        public bool DirectoryExists(string path) => P.DirectoryExists(Map(path));
        public FsEntry? GetEntry(string path) => P.GetEntry(Map(path)) is { } e ? Back(e) : null;
        public IEnumerable<FsEntry> Enumerate(string directory) => P.Enumerate(Map(directory)).Select(Back);
        public void CreateDirectory(string path) => P.CreateDirectory(Map(path));
        public void DeleteFile(string path) => P.DeleteFile(Map(path));
        public void DeleteDirectory(string path, bool recursive) => P.DeleteDirectory(Map(path), recursive);
        public void MoveFile(string source, string destination, bool overwrite) => P.MoveFile(Map(source), Map(destination), overwrite);
        public void MoveDirectory(string source, string destination) => P.MoveDirectory(Map(source), Map(destination));
        public Stream OpenRead(string path) => P.OpenRead(Map(path));
        public Stream Create(string path) => P.Create(Map(path));
        public void Flush(Stream stream) => P.Flush(stream);
        public void SetTimes(string path, DateTime creationUtc, DateTime lastWriteUtc) => P.SetTimes(Map(path), creationUtc, lastWriteUtc);
        public void SetAttributes(string path, FileAttributes attributes) => P.SetAttributes(Map(path), attributes);
        public (long Free, long Total)? GetSpace(string path) => P.GetSpace(Map(path));
    }

    [Fact]
    public async Task Status_restore_listing_and_runs_touch_the_smb_target_only_inside_a_lease()
    {
        using var d = new TempDir();
        var share = d.Sub("share");
        Directory.CreateDirectory(share);
        var src = d.Sub("src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "a.txt"), "v1");
        var cfg = TestConfigs.Jobs(1);
        cfg.Targets = [new TargetConfig { Id = "t", Type = TargetTypes.Smb, Path = @"\\nas01\backup", Username = @"NAS01\bf", PasswordRef = "target:t" }];
        cfg.Jobs[0].Sources[0].Path = src;
        cfg.Jobs[0].Target = new JobTargetRef { TargetId = "t", Subdir = "Docs" };
        cfg.Jobs[0].Files!.Vss = VssModes.Never;

        var connector = new CountingConnector();
        var guard = new LeaseGuardFileSystem(new MappedFs(share)) { ThrowOnViolation = true, HasLease = _ => connector.Active > 0 };
        await using var h = new RuntimeHarness(cfg);
        h.Secrets.Set("target:t", "test-password-123");
        await h.StartAsync([new FileJobEngine(guard, connector, NoSnapshotProvider.Instance, new FileEngineOptions { RetryDelays = [TimeSpan.Zero] }), new FileRestoreEngine(guard, connector)],
            fileSystem: guard, connector: connector);
        Assert.True(h.Runtime.NeedsLease("nas01"));

        // two runs: the second versions a.txt
        var first = await h.Runtime.Coordinator.Enqueue(new WorkRequest { JobId = "j1", Trigger = RunTriggers.Manual }).Completion;
        Assert.Equal(RunResults.Ok, first!.Result);
        File.WriteAllText(Path.Combine(src, "a.txt"), "v2 longer");
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        var second = await h.Runtime.Coordinator.Enqueue(new WorkRequest { JobId = "j1", Trigger = RunTriggers.Manual }).Completion;
        Assert.Equal(RunResults.Ok, second!.Result);
        Assert.Equal(0, connector.Active);

        // the minutely status: Versions info from the kv, no target access (the guard would throw), no lease taken
        var leasesBefore = connector.Total;
        var status = h.Runtime.BuildStatus();
        var versions = status.Jobs.Single(j => j.Id == "j1").Versions!;
        Assert.Equal(1, versions.Folders);
        Assert.NotNull(versions.Oldest);
        Assert.Equal(leasesBefore, connector.Total);
        Assert.NotNull(h.Runtime.History.GetValue(VersionsState.Key("j1")));

        // restore listing: inside a lease
        await using var c = await h.Connect();
        var points = (await c.CallAsync(IpcOps.RestoreVersions, new JsonObject { ["job_id"] = "j1" }))!.AsArray();
        Assert.Equal(2, points.Count);                                    // current + one Versions stamp
        var listing = (await c.CallAsync(IpcOps.RestoreBrowse, new JsonObject { ["job_id"] = "j1", ["point"] = "current", ["path"] = "" }))!.AsArray();
        Assert.Contains(listing, e => e!["name"]!.ToString() == "a.txt");
        var check = await c.CallAsync(IpcOps.PathCheck, new JsonObject { ["path"] = @"\\nas01\backup\Docs" });
        Assert.NotNull(check);
        Assert.True(connector.Total >= leasesBefore + 3);
        Assert.Equal(0, connector.Active);                                 // every lease released
        Assert.Equal(0, guard.Violations);
    }

    [Fact]
    public void Versions_state_round_trip_and_moved_folder()
    {
        var kv = new MemoryKeyValueStore();
        var cfg = TestConfigs.Jobs(1);
        var job = cfg.Jobs[0];
        Assert.Null(VersionsState.Read(kv, cfg, job)!.Folders);          // not run yet: the path, no numbers
        var path = VersionsState.Read(kv, cfg, job)!.Path;
        VersionsState.Write(kv, "j1", path, 3, new DateTime(2026, 11, 1, 6, 15, 0));
        var v = VersionsState.Read(kv, cfg, job)!;
        Assert.Equal(3, v.Folders);
        Assert.Equal(new DateTime(2026, 11, 1, 6, 15, 0), v.Oldest!.Value.DateTime);
        job.Target!.Subdir = "elsewhere";                                  // the configured folder moved: old numbers do not apply
        Assert.Null(VersionsState.Read(kv, cfg, job)!.Folders);
    }
}
