using System.Text.Json;
using System.Text.Json.Serialization;

namespace BackupForge.Engine.Restic;

// restic 0.20.0 --json output (doc/075_scripting.rst). Unknown fields are ignored; missing fields keep defaults.

public sealed class ResticStatus
{
    [JsonPropertyName("percent_done")] public double PercentDone { get; set; }
    [JsonPropertyName("total_files")] public long TotalFiles { get; set; }
    [JsonPropertyName("files_done")] public long FilesDone { get; set; }
    [JsonPropertyName("total_bytes")] public long TotalBytes { get; set; }
    [JsonPropertyName("bytes_done")] public long BytesDone { get; set; }
    [JsonPropertyName("error_count")] public long ErrorCount { get; set; }
    [JsonPropertyName("seconds_remaining")] public long SecondsRemaining { get; set; }
}

public sealed class ResticBackupSummary
{
    [JsonPropertyName("files_new")] public long FilesNew { get; set; }
    [JsonPropertyName("files_changed")] public long FilesChanged { get; set; }
    [JsonPropertyName("files_unmodified")] public long FilesUnmodified { get; set; }
    [JsonPropertyName("data_added")] public long DataAdded { get; set; }
    [JsonPropertyName("data_added_packed")] public long DataAddedPacked { get; set; }
    [JsonPropertyName("total_files_processed")] public long TotalFilesProcessed { get; set; }
    [JsonPropertyName("total_bytes_processed")] public long TotalBytesProcessed { get; set; }
    [JsonPropertyName("total_duration")] public double TotalDuration { get; set; }
    [JsonPropertyName("snapshot_id")] public string? SnapshotId { get; set; }
    [JsonPropertyName("dry_run")] public bool DryRun { get; set; }
}

public sealed class ResticError
{
    public string Message { get; set; } = "";
    public string? Item { get; set; }
    public string? During { get; set; }
}

public sealed class ResticSnapshot
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("short_id")] public string ShortId { get; set; } = "";
    [JsonPropertyName("time")] public DateTimeOffset Time { get; set; }
    [JsonPropertyName("hostname")] public string Hostname { get; set; } = "";
    [JsonPropertyName("paths")] public List<string> Paths { get; set; } = [];
    [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
    [JsonPropertyName("program_version")] public string? ProgramVersion { get; set; }
}

public sealed class ResticNode
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("mtime")] public DateTimeOffset? Mtime { get; set; }
}

public sealed class ResticCheckSummary
{
    [JsonPropertyName("num_errors")] public long NumErrors { get; set; }
    [JsonPropertyName("broken_packs")] public List<string>? BrokenPacks { get; set; }
    [JsonPropertyName("suggest_repair_index")] public bool SuggestRepairIndex { get; set; }
    [JsonPropertyName("suggest_prune")] public bool SuggestPrune { get; set; }
}

public sealed class ResticForgetGroup
{
    [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
    [JsonPropertyName("host")] public string? Host { get; set; }
    [JsonPropertyName("keep")] public List<ResticSnapshot>? Keep { get; set; }
    [JsonPropertyName("remove")] public List<ResticSnapshot>? Remove { get; set; }
}

public sealed class ResticRestoreSummary
{
    [JsonPropertyName("total_files")] public long TotalFiles { get; set; }
    [JsonPropertyName("files_restored")] public long FilesRestored { get; set; }
    [JsonPropertyName("files_skipped")] public long FilesSkipped { get; set; }
    [JsonPropertyName("total_bytes")] public long TotalBytes { get; set; }
    [JsonPropertyName("bytes_restored")] public long BytesRestored { get; set; }
}

public sealed record ResticExitError(int Code, string Message);

/// <summary>Parses restic's JSON lines (stdout) and JSON errors (stderr).</summary>
public static class ResticParser
{
    public static string? MessageType(string line)
    {
        if (!line.StartsWith('{')) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.TryGetProperty("message_type", out var mt) ? mt.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    public static T? Parse<T>(string line) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(line); }
        catch (JsonException) { return null; }
    }

    /// <summary>A stderr line: {"message_type":"error","error":{"message":…},"item":…} or the final exit_error.</summary>
    public static ResticError? ParseError(string line)
    {
        if (!line.StartsWith('{')) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            if (!r.TryGetProperty("message_type", out var mt) || mt.GetString() != "error") return null;
            var msg = r.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var em)
                ? em.GetString()
                : r.TryGetProperty("message", out var m) ? m.GetString() : null;
            return new ResticError
            {
                Message = msg ?? line,
                Item = r.TryGetProperty("item", out var it) ? it.GetString() : null,
                During = r.TryGetProperty("during", out var d) ? d.GetString() : null,
            };
        }
        catch (JsonException) { return null; }
    }

    public static ResticExitError? ParseExitError(string line)
    {
        if (!line.StartsWith('{')) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            if (!r.TryGetProperty("message_type", out var mt) || mt.GetString() != "exit_error") return null;
            return new ResticExitError(r.TryGetProperty("code", out var c) ? c.GetInt32() : -1, r.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "");
        }
        catch (JsonException) { return null; }
    }

    public static List<ResticSnapshot> Snapshots(string json) =>
        JsonSerializer.Deserialize<List<ResticSnapshot>>(json) ?? [];

    public static List<ResticForgetGroup> ForgetGroups(string json)
    {
        // forget prints the JSON array on the first line; prune output may follow
        var first = json.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(l => l.TrimStart().StartsWith('['));
        return first is null ? [] : JsonSerializer.Deserialize<List<ResticForgetGroup>>(first) ?? [];
    }

    /// <summary>"lock was created at … (7h12m3s ago)" → 7:12:03.</summary>
    public static TimeSpan? LockAge(string message)
    {
        var m = System.Text.RegularExpressions.Regex.Match(message, @"\((?:(\d+)h)?(?:(\d+)m)?(?:(\d+(?:\.\d+)?)s)? ago\)");
        if (!m.Success || m.Length <= 6) return null;
        double h = m.Groups[1].Success ? double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        double mi = m.Groups[2].Success ? double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        double s = m.Groups[3].Success ? double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        return TimeSpan.FromHours(h) + TimeSpan.FromMinutes(mi) + TimeSpan.FromSeconds(s);
    }

    /// <summary>"… locked by PID 4120 on SRV01 by …" → SRV01.</summary>
    public static string? LockHost(string message)
    {
        var m = System.Text.RegularExpressions.Regex.Match(message, @"PID \d+ on (\S+) by");
        return m.Success ? m.Groups[1].Value : null;
    }
}
