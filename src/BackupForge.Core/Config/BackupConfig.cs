using System.Text.Json.Serialization;

namespace BackupForge.Core.Config;

/// <summary>Root of config.json (schema 1). Mutable POCOs so the GUI can edit them; validation is separate.</summary>
public sealed class BackupConfig
{
    public const int CurrentSchema = 1;

    public int Schema { get; set; } = CurrentSchema;
    public string InstanceName { get; set; } = "";
    public string Language { get; set; } = "hu";
    public GeneralSettings General { get; set; } = new();
    public List<TargetConfig> Targets { get; set; } = [];
    public List<ResticRepoConfig> ResticRepos { get; set; } = [];
    public List<JobConfig> Jobs { get; set; } = [];
    public NotificationSettings Notifications { get; set; } = new();

    public JobConfig? FindJob(string id) => Jobs.FirstOrDefault(j => string.Equals(j.Id, id, StringComparison.Ordinal));
    public TargetConfig? FindTarget(string id) => Targets.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));
    public ResticRepoConfig? FindRepo(string id) => ResticRepos.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal));
}

public sealed class GeneralSettings
{
    public int MaxParallelJobs { get; set; } = 2;
    public int HistoryRetentionDays { get; set; } = 400;
    public int LogRetentionDays { get; set; } = 30;
}

public static class TargetTypes
{
    public const string Local = "local";
    public const string Smb = "smb";
    public static readonly string[] All = [Local, Smb];
}

public sealed class TargetConfig
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = TargetTypes.Local;
    public string Path { get; set; } = "";
    public string? Username { get; set; }
    public string? PasswordRef { get; set; }
    public double? MinFreeGb { get; set; }
}

public sealed class ResticRepoConfig
{
    public string Id { get; set; } = "";
    public string Location { get; set; } = "";
    public string PasswordRef { get; set; } = "";
    /// <summary>Environment variable name → secret reference (e.g. AWS_SECRET_ACCESS_KEY → restic:offsite:s3key).</summary>
    public Dictionary<string, string> EnvRefs { get; set; } = [];
    public bool AppendOnlyServer { get; set; }
    public ResticRetention Retention { get; set; } = new();
    public ResticMaintenance Maintenance { get; set; } = new();
}

public sealed class ResticRetention
{
    public int? KeepLast { get; set; } = 3;
    public int? KeepDaily { get; set; } = 14;
    public int? KeepWeekly { get; set; } = 8;
    public int? KeepMonthly { get; set; } = 12;
    public int? KeepYearly { get; set; } = 2;
}

public sealed class ResticMaintenance
{
    public string? ForgetPrune { get; set; } = "weekly Sun 03:00";
    public string? Check { get; set; } = "weekly Sat 05:00";
    public string CheckReadDataSubset { get; set; } = "5%";
}

public static class JobTypes
{
    public const string Files = "files";
    public const string Restic = "restic";
    public const string Sql = "sql";
    public static readonly string[] All = [Files, Restic, Sql];
}

public sealed class JobConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = JobTypes.Files;
    public bool Enabled { get; set; } = true;
    public ScheduleConfig Schedule { get; set; } = new();
    public List<SourceConfig> Sources { get; set; } = [];
    public JobTargetRef? Target { get; set; }
    public FilesOptions? Files { get; set; }
    public ResticJobOptions? Restic { get; set; }
    public SqlJobOptions? Sql { get; set; }
    public double? ExpectedIntervalH { get; set; }
    public string Notify { get; set; } = "default";
    public List<string> OnSuccess { get; set; } = [];
    public bool CatchUp { get; set; } = true;
    public int? TimeoutMin { get; set; }

    [JsonIgnore]
    public int EffectiveTimeoutMin => TimeoutMin ?? Type switch
    {
        JobTypes.Restic => 1440,
        JobTypes.Sql => 360,
        _ => 720,
    };
}

public static class ScheduleKinds
{
    public const string Interval = "interval";
    public const string Daily = "daily";
    public const string Weekly = "weekly";
    public const string Monthly = "monthly";
    public const string Chained = "chained";
    public const string Manual = "manual";
    public static readonly string[] All = [Interval, Daily, Weekly, Monthly, Chained, Manual];
}

public sealed class ScheduleConfig
{
    public string Kind { get; set; } = ScheduleKinds.Manual;
    public int? EveryMin { get; set; }
    public string? Start { get; set; }
    public TimeWindow? Window { get; set; }
    public List<string> Times { get; set; } = [];
    public List<string> Days { get; set; } = [];
    public string? Day { get; set; }
    public string? Time { get; set; }
    [JsonConverter(typeof(IntOrStringConverter))]
    public string? DayOfMonth { get; set; }
}

public sealed class TimeWindow
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

public sealed class SourceConfig
{
    public string Path { get; set; } = "";
    public List<string> Excludes { get; set; } = [];
}

public sealed class JobTargetRef
{
    public string TargetId { get; set; } = "";
    public string Subdir { get; set; } = "";
}

public static class FilesModes
{
    public const string Mirror = "mirror";
    public const string Update = "update";
    public static readonly string[] All = [Mirror, Update];
}

public static class VssModes
{
    public const string Never = "never";
    public const string WhenAvailable = "when_available";
    public const string Required = "required";
    public static readonly string[] All = [Never, WhenAvailable, Required];
}

public static class CompareModes
{
    public const string SizeMtime = "size_mtime";
    public const string Content = "content";
    public static readonly string[] All = [SizeMtime, Content];
}

public static class CloudFilesModes
{
    public const string Skip = "skip";
    public const string Fail = "fail";
    public static readonly string[] All = [Skip, Fail];
}

public sealed class FilesOptions
{
    public string Mode { get; set; } = FilesModes.Mirror;
    public VersioningOptions Versioning { get; set; } = new();
    public string Vss { get; set; } = VssModes.WhenAvailable;
    public int DeleteGuardPct { get; set; } = 20;
    public int DeleteGuardMinFiles { get; set; } = 50;
    public string Compare { get; set; } = CompareModes.SizeMtime;
    public int Parallelism { get; set; } = 4;
    public double? ThrottleMbps { get; set; }
    public string CloudFiles { get; set; } = CloudFilesModes.Skip;
    public bool UseDefaultExcludes { get; set; } = true;
}

public sealed class VersioningOptions
{
    public bool Enabled { get; set; } = true;
    public string? Folder { get; set; }
    public int KeepDays { get; set; } = 21;
    public int? MaxVersionsPerFile { get; set; }
}

public sealed class ResticJobOptions
{
    public string RepoId { get; set; } = "";
    public string Vss { get; set; } = VssModes.WhenAvailable;
    public List<string> Tags { get; set; } = [];
}

public static class SqlAuthModes
{
    public const string Windows = "windows";
    public const string Sql = "sql";
    public static readonly string[] All = [Windows, Sql];
}

public static class SqlDbModes
{
    public const string AllUser = "all_user";
    public const string Selected = "selected";
    public static readonly string[] All = [AllUser, Selected];
}

public static class SqlCompressionModes
{
    public const string Auto = "auto";
    public const string On = "on";
    public const string Off = "off";
    public static readonly string[] All = [Auto, On, Off];
}

public sealed class SqlJobOptions
{
    public string Instance { get; set; } = "";
    public string Auth { get; set; } = SqlAuthModes.Windows;
    public string? Username { get; set; }
    public string? LoginRef { get; set; }
    public SqlDatabaseSelection Databases { get; set; } = new();
    public bool Verify { get; set; } = true;
    public bool CopyOnly { get; set; }
    public string Compression { get; set; } = SqlCompressionModes.Auto;
    public int KeepLocal { get; set; } = 2;
    public string? Checkdb { get; set; }
    public string? StagingPath { get; set; }
    public List<string> IgnoreFullRecovery { get; set; } = [];
}

public sealed class SqlDatabaseSelection
{
    public string Mode { get; set; } = SqlDbModes.AllUser;
    public List<string> Include { get; set; } = [];
    public List<string> Exclude { get; set; } = [];
    public bool IncludeSystem { get; set; } = true;
}

public sealed class NotificationSettings
{
    public EmailSettings Email { get; set; } = new();
    public bool EventLog { get; set; } = true;
    public string? HeartbeatUrl { get; set; }
}

public static class SmtpSecurityModes
{
    public const string None = "none";
    public const string StartTls = "starttls";
    public const string SslOnConnect = "ssl";
    public static readonly string[] All = [None, StartTls, SslOnConnect];
}

public sealed class EmailSettings
{
    public bool Enabled { get; set; }
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public string Security { get; set; } = SmtpSecurityModes.StartTls;
    public string? Username { get; set; }
    public string? PasswordRef { get; set; }
    public string From { get; set; } = "";
    public List<string> To { get; set; } = [];
    public bool OnFailure { get; set; } = true;
    public bool OnWarning { get; set; } = true;
    public bool OnRecovery { get; set; } = true;
    public string? WeeklyReport { get; set; } = "Sun 06:30";
}
