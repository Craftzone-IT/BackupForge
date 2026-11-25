using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BackupForge.Core.Status;

// status.json, status_schema 1 — the contract is docs/STATUS-CONTRACT.md, the machine schema schema/status.schema.json.
// Do not remove fields or change their meaning without bumping StatusSchema.

public sealed class StatusDocument
{
    public const int CurrentSchema = 1;

    public int StatusSchema { get; set; } = CurrentSchema;
    public string App { get; set; } = "BackupForge";
    public string AppVersion { get; set; } = "";
    public string InstanceName { get; set; } = "";
    public string Hostname { get; set; } = "";
    public ServiceStatus Service { get; set; } = new();
    public List<JobStatus> Jobs { get; set; } = [];
    public List<RepoStatus> Repos { get; set; } = [];
    public List<TargetStatus> Targets { get; set; } = [];
    public NotificationStatus Notifications { get; set; } = new();
}

public sealed class ServiceStatus
{
    public DateTimeOffset Started { get; set; }
    public DateTimeOffset Heartbeat { get; set; }
    public bool ConfigOk { get; set; } = true;
    public string? ConfigError { get; set; }
    public DateTimeOffset? ConfigChanged { get; set; }
}

public sealed class JobStatus
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public bool Enabled { get; set; }
    public DateTimeOffset? DisabledSince { get; set; }
    public string ScheduleText { get; set; } = "";
    public double? ExpectedIntervalH { get; set; }
    public DateTimeOffset? NextRun { get; set; }
    public RunningStatus? Running { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ChainStatus? Chain { get; set; }
    public LastRunStatus? LastRun { get; set; }
    public DateTimeOffset? LastOk { get; set; }
    public DateTimeOffset? LastNotOk { get; set; }
    [JsonPropertyName("runs_24h")]
    public int Runs24h { get; set; }
    [JsonPropertyName("not_ok_24h")]
    public int NotOk24h { get; set; }
    public bool Stale { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JobTargetStatus? Target { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VersionsStatus? Versions { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DeleteGuardStatus? DeleteGuard { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SqlStatus? Sql { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResticJobStatus? Restic { get; set; }
}

public sealed class RunningStatus
{
    public DateTimeOffset Since { get; set; }
    public string Phase { get; set; } = "";
    public double? Percent { get; set; }
}

public sealed class ChainStatus
{
    public List<string> OnSuccess { get; set; } = [];
}

public sealed class LastRunStatus
{
    public long RunId { get; set; }
    public string Trigger { get; set; } = "";
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset? End { get; set; }
    public string Result { get; set; } = "";
    public string Summary { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? FilesCopied { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? FilesVersioned { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? FilesDeleted { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? FilesSkipped { get; set; }
    public long Bytes { get; set; }
    public List<string> Errors { get; set; } = [];
}

public sealed class JobTargetStatus
{
    public string Path { get; set; } = "";
    public bool Online { get; set; }
    public double? FreeGb { get; set; }
    public double? SizeGb { get; set; }
    public double? MinFreeGb { get; set; }
}

public sealed class VersionsStatus
{
    public string Path { get; set; } = "";
    public int KeepDays { get; set; }
    public int? Folders { get; set; }
    public DateTimeOffset? Oldest { get; set; }
    public double? SizeGb { get; set; }
}

public sealed class DeleteGuardStatus
{
    public bool Tripped { get; set; }
    public bool PendingApproval { get; set; }
}

public sealed class SqlStatus
{
    public string Instance { get; set; } = "";
    public string? Edition { get; set; }
    public string? Version { get; set; }
    public List<SqlDbStatus> Dbs { get; set; } = [];
    public StagingStatus? Staging { get; set; }
}

public sealed class SqlDbStatus
{
    public string Name { get; set; } = "";
    public bool System { get; set; }
    public string? RecoveryModel { get; set; }
    public string? LastResult { get; set; }
    public DateTimeOffset? LastOk { get; set; }
    public long? LastSize { get; set; }
    public long? MedianOkSize { get; set; }
    public bool? VerifyOk { get; set; }
    public DateTimeOffset? LastFullAny { get; set; }
    public DateTimeOffset? LastCheckdb { get; set; }
    public bool? LastCheckdbOk { get; set; }
    public string? Error { get; set; }
}

public sealed class StagingStatus
{
    public string Path { get; set; } = "";
    public double? FreeGb { get; set; }
}

public sealed class ResticJobStatus
{
    public string RepoId { get; set; } = "";
    public string LocationDisplay { get; set; } = "";
    public string? LastSnapshot { get; set; }
    public int? Snapshots { get; set; }
}

public sealed class RepoStatus
{
    public string Id { get; set; } = "";
    public string LocationDisplay { get; set; } = "";
    public bool AppendOnlyServer { get; set; }
    public DateTimeOffset? LastForgetPrune { get; set; }
    public string? LastForgetPruneResult { get; set; }
    public DateTimeOffset? LastCheck { get; set; }
    public string? LastCheckResult { get; set; }
    public double? LastCheckReadPct { get; set; }
    public string? ResticVersion { get; set; }
}

public sealed class TargetStatus
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Online { get; set; }
    public double? FreeGb { get; set; }
    public double? SizeGb { get; set; }
    public double? MinFreeGb { get; set; }
    public DateTimeOffset? Checked { get; set; }
    public string? Error { get; set; }
}

public sealed class NotificationStatus
{
    public bool EmailEnabled { get; set; }
    public DateTimeOffset? LastSent { get; set; }
    public string? LastError { get; set; }
}

/// <summary>ISO 8601 with offset, whole seconds: 2026-11-24T14:30:00+01:00 (as in the contract).</summary>
public sealed class IsoOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateTimeOffset.Parse(reader.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture));
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true,
    Converters = [typeof(IsoOffsetConverter)])]
[JsonSerializable(typeof(StatusDocument))]
public sealed partial class StatusJsonContext : JsonSerializerContext;

public static class StatusJson
{
    public static string Serialize(StatusDocument doc) => JsonSerializer.Serialize(doc, StatusJsonContext.Default.StatusDocument);
    public static StatusDocument? Parse(string json) => JsonSerializer.Deserialize(json, StatusJsonContext.Default.StatusDocument);
}
