namespace BackupForge.Core.Execution;

/// <summary>Runtime locations under C:\ProgramData\BackupForge (SPEC 3. "Futásidejű helyek").</summary>
public sealed class DataPaths
{
    public const string DataDirEnvVar = "BACKUPFORGE_DATA";

    public DataPaths(string root) => Root = root;

    public string Root { get; }
    public string ConfigFile => Path.Combine(Root, "config.json");
    public string SecretsFile => Path.Combine(Root, "secrets.dat");
    public string HistoryDb => Path.Combine(Root, "history.db");
    public string StatusDir => Path.Combine(Root, "status");
    public string StatusFile => Path.Combine(StatusDir, "status.json");
    public string LogsDir => Path.Combine(Root, "logs");
    public string JobLogsDir => Path.Combine(LogsDir, "jobs");
    public string JobLogDir(string jobId) => Path.Combine(JobLogsDir, jobId);
    public string RunLogFile(string jobId, long runId) => Path.Combine(JobLogDir(jobId), $"{runId}.log");
    public string StagingRoot => Path.Combine(Root, "staging");
    public string ResticCache => Path.Combine(Root, "restic-cache");

    /// <summary>%ProgramData%\BackupForge, or the BACKUPFORGE_DATA environment variable (tests / development).</summary>
    public static DataPaths Default()
    {
        var env = Environment.GetEnvironmentVariable(DataDirEnvVar);
        if (!string.IsNullOrWhiteSpace(env)) return new DataPaths(env);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return new DataPaths(Path.Combine(programData, "BackupForge"));
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(StatusDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(JobLogsDir);
        Directory.CreateDirectory(StagingRoot);
    }
}
