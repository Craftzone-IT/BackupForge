using BackupForge.Core.Abstractions;
using BackupForge.Core.Config;
using BackupForge.Core.Execution;
using BackupForge.Engine.Files;
using BackupForge.TestKit;

namespace BackupForge.Engine.Files.Tests;

public sealed class EngineHarness : IDisposable
{
    public TempDir Dir { get; } = new();
    public string Src { get; }
    public string TargetRoot { get; }
    public string Mirror => Path.Combine(TargetRoot, "Docs");
    public string Versions => Path.Combine(TargetRoot, "Docs_Versions");
    public BackupConfig Config { get; }
    public JobConfig Job => Config.Jobs[0];
    public ManualClock Clock { get; } = new(new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc);
    public MemoryKeyValueStore State { get; } = new();
    public MemoryRunLog Log { get; private set; } = new();
    public IFileSystem Fs { get; set; } = PhysicalFileSystem.Instance;
    public FileEngineOptions Options { get; set; } = new() { RetryDelays = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero] };
    public ISnapshotProvider Vss { get; set; } = NoSnapshotProvider.Instance;
    private long _runId;

    public EngineHarness(string mode = FilesModes.Mirror)
    {
        Src = Dir.Sub("src");
        TargetRoot = Dir.Sub("target");
        Directory.CreateDirectory(Src);
        Directory.CreateDirectory(TargetRoot);
        Config = new BackupConfig
        {
            InstanceName = "SRV01",
            Language = "en",
            Targets = [new TargetConfig { Id = "t", Type = TargetTypes.Local, Path = TargetRoot }],
            Jobs =
            [
                new JobConfig
                {
                    Id = "docs", Name = "Docs", Type = JobTypes.Files,
                    Sources = [new SourceConfig { Path = Src }],
                    Target = new JobTargetRef { TargetId = "t", Subdir = "Docs" },
                    Files = new FilesOptions { Mode = mode, Vss = VssModes.Never, Parallelism = 4 },
                },
            ],
        };
    }

    public string Write(string rel, string content = "x", DateTime? mtimeUtc = null, string? root = null)
    {
        var p = Path.Combine(root ?? Src, rel.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        File.SetLastWriteTimeUtc(p, mtimeUtc ?? new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc));
        return p;
    }

    public string InMirror(string rel) => Path.Combine(Mirror, rel.Replace('\\', Path.DirectorySeparatorChar));

    public async Task<RunOutcome> RunAsync(RunOptions? options = null)
    {
        Log = new MemoryRunLog();
        var engine = new FileJobEngine(Fs, NoOpTargetConnector.Instance, Vss, Options);
        var ctx = new JobRunContext
        {
            Config = Config,
            Job = Job,
            Request = new WorkRequest { JobId = Job.Id, Trigger = RunTriggers.Manual, Options = options ?? RunOptions.Default },
            RunId = ++_runId,
            Log = Log,
            Progress = new Progress<RunProgress>(),
            Clock = Clock,
            Secrets = new InMemorySecretStore(),
            Paths = new DataPaths(Dir.Sub("data")),
            State = State,
        };
        var o = await engine.RunAsync(ctx, CancellationToken.None);
        Clock.Advance(TimeSpan.FromMinutes(10)); // next run gets its own Versions stamp
        return o;
    }

    public List<string> VersionFolders() =>
        Directory.Exists(Versions) ? [.. Directory.GetDirectories(Versions).Select(Path.GetFileName).Order()!] : [];

    public void Dispose() => Dir.Dispose();
}
