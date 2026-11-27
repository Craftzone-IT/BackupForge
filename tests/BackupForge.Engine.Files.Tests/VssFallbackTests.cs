using BackupForge.Core.Abstractions;
using BackupForge.Core.Config;
using BackupForge.Core.Execution;

namespace BackupForge.Engine.Files.Tests;

/// <summary>CR-001 2.: a snapshot taken without the writers (crash-consistent fallback) turns the run into a warning.</summary>
public class VssFallbackTests
{
    private sealed class FakeVss(params string[] reasons) : ISnapshotProvider
    {
        public bool IsSupported => true;
        public ISnapshotSession Create(IReadOnlyCollection<string> paths, CancellationToken ct) => new Session(reasons);
        public IReadOnlyList<string> DeleteOrphans(IEnumerable<string> snapshotIds) => [];

        private sealed class Session(string[] reasons) : ISnapshotSession
        {
            public string Translate(string path) => path; // reads the live files
            public IReadOnlyList<(string SnapshotId, string? SetId, string Volume)> Snapshots { get; } = [("{snap-1}", "{set-1}", "C:\\")];
            public IReadOnlyList<string> WritersSkippedReasons => reasons;
            public void Dispose() { }
        }
    }

    [Fact]
    public async Task Writerless_snapshot_is_a_warning()
    {
        using var h = new EngineHarness { Vss = new FakeVss("GatherWriterMetadata: timed out after 90 s") };
        h.Job.Files!.Vss = VssModes.WhenAvailable;
        h.Write("a.txt", "a");
        var o = await h.RunAsync();
        Assert.Equal(RunResults.Warning, o.Result);
        Assert.Contains(o.Items, i => i.Message.Contains("writers did not respond", StringComparison.Ordinal) && i.Message.Contains("timed out", StringComparison.Ordinal));
        Assert.Equal(1, o.FilesCopied);
    }

    [Fact]
    public async Task Snapshot_with_writers_stays_ok()
    {
        using var h = new EngineHarness { Vss = new FakeVss() };
        h.Job.Files!.Vss = VssModes.WhenAvailable;
        h.Write("a.txt", "a");
        var o = await h.RunAsync();
        Assert.Equal(RunResults.Ok, o.Result);
    }

    private sealed class BrokenVss : ISnapshotProvider
    {
        public bool IsSupported => true;
        public ISnapshotSession Create(IReadOnlyCollection<string> paths, CancellationToken ct) =>
            throw new VssSnapshotIncompleteException("snapshot {snap-1} of C:\\ has no device object",
                ["DoSnapshotSet: status 0x0004230A (FINISHED) at 3.1 s", "GetSnapshotProperties({snap-1}): 0x00000000", "  SnapshotId 00000000-0000-0000-0000-000000000000 (EMPTY — struct not filled)"]);
        public IReadOnlyList<string> DeleteOrphans(IEnumerable<string> snapshotIds) => [];
    }

    [Fact]
    public async Task Snapshot_without_device_logs_the_trace_at_error_level()
    {
        // CR-002 A1. 4.: not a silent "VSS unavailable" — the run log carries the HRESULTs and statuses
        using var h = new EngineHarness { Vss = new BrokenVss() };
        h.Job.Files!.Vss = VssModes.WhenAvailable;
        h.Write("a.txt", "a");
        var o = await h.RunAsync();
        Assert.Equal(RunResults.Warning, o.Result);
        Assert.Contains(h.Log.Lines, l => l.StartsWith("ERR VSS diagnostics:", StringComparison.Ordinal) && l.Contains("struct not filled", StringComparison.Ordinal));
    }
}
