using System.Globalization;
using System.Text.Json;
using BackupForge.Core.Abstractions;
using BackupForge.Core.Config;
using BackupForge.Core.Status;

namespace BackupForge.Engine.Files;

/// <summary>
/// CR-003 2.: the Versions folder count / oldest stamp for status.json and the weekly report. The run measures it at its
/// end, inside its own SMB lease, and keeps it in the kv (<c>versions_info:&lt;job&gt;</c>); the minutely status writer only
/// reads the kv and never touches the target.
/// </summary>
public static class VersionsState
{
    public static string Key(string jobId) => "versions_info:" + jobId;

    private sealed record Stored(string Path, int Folders, string? Oldest);

    public static void Write(IKeyValueStore state, string jobId, string path, int folders, DateTime? oldestLocal) =>
        state.SetValue(Key(jobId), JsonSerializer.Serialize(new Stored(path, folders, oldestLocal?.ToString("o", CultureInfo.InvariantCulture))));

    /// <summary>The configured Versions path with the last measured numbers (null numbers until the job ran, or after the folder moved).</summary>
    public static VersionsStatus? Read(IKeyValueStore state, BackupConfig config, JobConfig job)
    {
        if (job.Files is not { Versioning.Enabled: true } || job.Target is null || config.FindTarget(job.Target.TargetId) is not { } target) return null;
        if (JobPaths.VersionsPath(target, job.Target, job.Files.Versioning) is not { } path) return null;
        var status = new VersionsStatus { Path = path };
        Stored? s = null;
        try { s = state.GetValue(Key(job.Id)) is { } json ? JsonSerializer.Deserialize<Stored>(json) : null; }
        catch (JsonException) { }
        if (s is null || !string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase)) return status;
        status.Folders = s.Folders;
        if (s.Oldest is not null && DateTime.TryParse(s.Oldest, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var oldest))
            status.Oldest = new DateTimeOffset(oldest, TimeZoneInfo.Local.GetUtcOffset(oldest));
        return status;
    }
}
