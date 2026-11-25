using BackupForge.Core.Config;

namespace BackupForge.Core.Execution;

public static class RunResults
{
    public const string Ok = "ok";
    public const string Warning = "warning";
    public const string Failed = "failed";
    public const string Interrupted = "interrupted";
    public const string Skipped = "skipped";

    /// <summary>ok and warning count as successful (chains continue, last_ok).</summary>
    public static bool IsSuccess(string? result) => result is Ok or Warning;
    public static bool IsNotOk(string? result) => result is Failed or Interrupted;

    /// <summary>Severity order for combining results: ok &lt; warning &lt; failed.</summary>
    public static string Worst(string a, string b)
    {
        static int Rank(string r) => r switch { Ok => 0, Skipped => 0, Warning => 1, Interrupted => 2, Failed => 3, _ => 3 };
        return Rank(b) > Rank(a) ? b : a;
    }
}

public static class RunKinds
{
    public const string Backup = "backup";
    public const string ForgetPrune = "forget_prune";
    public const string Check = "check";
    public const string Checkdb = "checkdb";
    public const string Retention = "retention";
    public const string Restore = "restore";
}

public static class RunTriggers
{
    public const string Schedule = "schedule";
    public const string CatchUp = "catch_up";
    public const string Manual = "manual";
    public const string Chain = "chain";
}

public static class RunPhases
{
    public const string Plan = "plan";
    public const string Copy = "copy";
    public const string Retention = "retention";
    public const string Vss = "vss";
    public const string Backup = "backup";
    public const string Verify = "verify";
    public const string ForgetPrune = "forget_prune";
    public const string Check = "check";
    public const string Checkdb = "checkdb";
}

public static class ItemLevels
{
    public const string Error = "error";
    public const string Warning = "warning";
    public const string Info = "info";
}

public sealed record RunItem(string Level, string? Path, string Message);

/// <summary>What an engine reports at the end of a run.</summary>
public sealed class RunOutcome
{
    public const int MaxStoredItems = 50;

    public string Result { get; set; } = RunResults.Ok;
    public string Summary { get; set; } = "";
    public long FilesProcessed { get; set; }
    public long FilesCopied { get; set; }
    public long FilesVersioned { get; set; }
    public long FilesDeleted { get; set; }
    public long FilesSkipped { get; set; }
    public long BytesProcessed { get; set; }
    public long BytesTransferred { get; set; }
    public int ErrorCount { get; set; }
    public int WarningCount { get; set; }
    /// <summary>The first <see cref="MaxStoredItems"/> errors / warnings; the rest is only counted.</summary>
    public List<RunItem> Items { get; } = [];
    public string? DetailsJson { get; set; }
    public bool DeleteGuardTripped { get; set; }
    public List<SqlDbResult> SqlResults { get; } = [];

    public void AddError(string? path, string message)
    {
        ErrorCount++;
        if (Items.Count < MaxStoredItems) Items.Add(new RunItem(ItemLevels.Error, path, message));
    }

    public void AddWarning(string? path, string message)
    {
        WarningCount++;
        if (Items.Count < MaxStoredItems) Items.Add(new RunItem(ItemLevels.Warning, path, message));
    }

    public static RunOutcome Failed(string summary) => new() { Result = RunResults.Failed, Summary = summary };
}

public sealed class SqlDbResult
{
    public string Instance { get; set; } = "";
    public string Database { get; set; } = "";
    public bool System { get; set; }
    public string Result { get; set; } = RunResults.Ok;
    public string? BackupFile { get; set; }
    public long? SizeBytes { get; set; }
    public double? DurationS { get; set; }
    public bool? VerifyOk { get; set; }
    public string? Error { get; set; }
    public string? RecoveryModel { get; set; }
}

public sealed record RunProgress(string Phase, double? Percent, string? Message = null);

public sealed class RunOptions
{
    public bool DryRun { get; init; }
    public bool AllowMassDelete { get; init; }
    public static readonly RunOptions Default = new();
}

/// <summary>A unit of work for the coordinator: a job run or a repository maintenance task.</summary>
public sealed class WorkRequest
{
    public required string JobId { get; init; }
    public string Kind { get; init; } = RunKinds.Backup;
    public required string Trigger { get; init; }
    public RunOptions Options { get; init; } = RunOptions.Default;
    public string? ChainRunId { get; init; }
    /// <summary>Extra parameters for non-backup kinds (e.g. restore source / target), opaque to the coordinator.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();
}

/// <summary>Everything an engine gets for one run.</summary>
public sealed class JobRunContext
{
    public required BackupConfig Config { get; init; }
    /// <summary>The job (null for repository maintenance, see <see cref="Request"/>.JobId).</summary>
    public JobConfig? Job { get; init; }
    public required WorkRequest Request { get; init; }
    public required long RunId { get; init; }
    public required IRunLog Log { get; init; }
    public required IProgress<RunProgress> Progress { get; init; }
    public required Abstractions.IClock Clock { get; init; }
    public required Abstractions.ISecretStore Secrets { get; init; }
    public required DataPaths Paths { get; init; }
    /// <summary>Persistent engine state (delete-guard approval etc.).</summary>
    public Abstractions.IKeyValueStore State { get; init; } = new Abstractions.MemoryKeyValueStore();
    /// <summary>Records a VSS snapshot id for orphan cleanup (null outside the service).</summary>
    public Action<string, string?>? VssSnapshotCreated { get; init; }
    public Action<string>? VssSnapshotDeleted { get; init; }
    public RunOptions Options => Request.Options;
    /// <summary>Folders of all jobs and of BackupForge itself (CR-001 1b.): an engine never deletes inside another job's folder.</summary>
    public IReadOnlyList<Config.ProtectedPath> ProtectedPaths { get; init; } = [];
}

/// <summary>An engine for one job type and run kind.</summary>
public interface IJobEngine
{
    bool CanRun(string jobType, string kind);
    /// <summary>Resource locks this run needs besides the per-job lock (target / repository / SQL instance).</summary>
    IEnumerable<string> Locks(BackupConfig config, WorkRequest request) => [];
    Task<RunOutcome> RunAsync(JobRunContext context, CancellationToken cancellationToken);
}
