using System.Text;
using BackupForge.Core.Abstractions;
using BackupForge.Core.Execution;
using BackupForge.Core.History;
using BackupForge.Core.Resources;
using Microsoft.Extensions.Logging;

namespace BackupForge.Core.Config;

public sealed record ConfigLoadResult(bool Ok, bool Changed, string? Error, ValidationResult? Validation);

/// <summary>
/// Owns config.json (SPEC 4.): loads and validates it, keeps the last valid version when the file is broken,
/// saves atomically with config.json.bak.1..10 and records every change in history (config_changes).
/// </summary>
public sealed class ConfigStore
{
    public const int BackupCount = 10;

    private readonly DataPaths _paths;
    private readonly ISecretStore _secrets;
    private readonly HistoryStore _history;
    private readonly IClock _clock;
    private readonly ILogger _log;
    private readonly Lock _lock = new();

    public ConfigStore(DataPaths paths, ISecretStore secrets, HistoryStore history, IClock clock, ILogger logger)
    {
        _paths = paths;
        _secrets = secrets;
        _history = history;
        _clock = clock;
        _log = logger;
        Current = DefaultConfig();
    }

    /// <summary>The configuration in effect (always valid).</summary>
    public BackupConfig Current { get; private set; }

    /// <summary>Error of the file on disk if it is currently unusable (the service runs on <see cref="Current"/>).</summary>
    public string? Error { get; private set; }

    public bool IsFileValid => Error is null;

    public DateTimeOffset? LastChanged => _history.LastConfigChange()?.At;

    public static BackupConfig DefaultConfig() => new() { InstanceName = Environment.MachineName };

    /// <summary>Reads config.json. A missing file creates the default one.</summary>
    public ConfigLoadResult Load(string by = "manual-edit")
    {
        lock (_lock)
        {
            if (!File.Exists(_paths.ConfigFile))
            {
                var def = DefaultConfig();
                WriteAtomic(def);
                Current = def;
                Error = null;
                _initialised = true;
                _history.AddConfigChange(_clock.UtcNow, "service", "default configuration created", null);
                return new ConfigLoadResult(true, true, null, null);
            }

            BackupConfig parsed;
            try
            {
                parsed = ConfigJson.Load(_paths.ConfigFile);
            }
            catch (Exception ex) when (ex is ConfigFormatException or IOException or UnauthorizedAccessException)
            {
                Error = ex.Message;
                _log.LogError("config.json is invalid, keeping the last valid configuration: {Error}", ex.Message);
                return new ConfigLoadResult(false, false, ex.Message, null);
            }

            var v = ConfigValidator.Validate(parsed, _secrets, _paths.Root);
            if (!v.IsValid)
            {
                Error = string.Join("; ", v.Errors.Select(e => $"{e.Path}: {e.Message(Texts.CultureFor(parsed.Language))}"));
                _log.LogError("config.json is invalid, keeping the last valid configuration: {Error}", Error);
                return new ConfigLoadResult(false, false, Error, v);
            }

            Error = null;
            var changes = ConfigDiff.Describe(Current, parsed);
            var changed = changes.Count > 0 || ConfigJson.Serialize(Current) != ConfigJson.Serialize(parsed);
            if (changed && !IsInitial(Current))
                Record(by, changes, parsed);
            Current = parsed;
            return new ConfigLoadResult(true, changed, null, v);
        }
    }

    private bool _initialised;
    private bool IsInitial(BackupConfig _)
    {
        // the very first load after service start is not a change
        if (_initialised) return false;
        _initialised = true;
        return true;
    }

    /// <summary>Validates and saves a new configuration (GUI / CLI). Returns the validation result; nothing is written when invalid.</summary>
    public ValidationResult Save(BackupConfig config, string by)
    {
        lock (_lock)
        {
            var v = ConfigValidator.Validate(config, _secrets, _paths.Root);
            if (!v.IsValid) return v;
            foreach (var w in v.Warnings) _log.LogWarning("configuration warning: {Path}: {Message}", w.Path, w.Message(Texts.CultureFor(config.Language)));
            _initialised = true;
            var changes = ConfigDiff.Describe(Current, config);
            RotateBackups();
            WriteAtomic(config);
            Record(by, changes, config);
            Current = ConfigJson.Clone(config);
            Error = null;
            return v;
        }
    }

    private void Record(string by, List<string> changes, BackupConfig config)
    {
        var summary = changes.Count == 0 ? "configuration saved (no semantic change)" : string.Join("; ", changes);
        _history.AddConfigChange(_clock.UtcNow, by, summary, System.Text.Json.JsonSerializer.Serialize(changes));
        foreach (var j in config.Jobs)
        {
            var key = "disabled_since:" + j.Id;
            var was = _history.GetTime(key);
            if (!j.Enabled && was is null) _history.SetTime(key, _clock.UtcNow);
            else if (j.Enabled && was is not null) _history.SetTime(key, null);
        }
        _log.LogInformation("configuration changed by {By}: {Summary}", by, summary);
    }

    /// <summary>When a job was disabled (SPEC 9.4); null if enabled or unknown.</summary>
    public DateTimeOffset? DisabledSince(string jobId) => _history.GetTime("disabled_since:" + jobId);

    private void RotateBackups()
    {
        if (!File.Exists(_paths.ConfigFile)) return;
        var last = _paths.ConfigFile + ".bak." + BackupCount;
        if (File.Exists(last)) File.Delete(last);
        for (var i = BackupCount - 1; i >= 1; i--)
        {
            var from = _paths.ConfigFile + ".bak." + i;
            if (File.Exists(from)) File.Move(from, _paths.ConfigFile + ".bak." + (i + 1));
        }
        File.Copy(_paths.ConfigFile, _paths.ConfigFile + ".bak.1");
    }

    private void WriteAtomic(BackupConfig config) => AtomicFile.Write(_paths.ConfigFile, ConfigJson.ToUtf8(config));
}

/// <summary>Write-to-temp + replace in the same folder (readers never see a half-written file).</summary>
public static class AtomicFile
{
    public static void Write(string path, byte[] content)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            fs.Write(content);
            fs.Flush(flushToDisk: true);
        }
        if (File.Exists(path))
        {
            try
            {
                File.Replace(tmp, path, null, ignoreMetadataErrors: true);
                return;
            }
            catch (PlatformNotSupportedException) { }
            catch (IOException)
            {
                // e.g. File.Replace across volumes / on some network shares: fall back to move-overwrite
            }
        }
        File.Move(tmp, path, overwrite: true);
    }

    public static void WriteText(string path, string text) => Write(path, new UTF8Encoding(false).GetBytes(text));
}

/// <summary>Human-readable summary of what changed between two configurations (config_changes, weekly report).</summary>
public static class ConfigDiff
{
    public static List<string> Describe(BackupConfig before, BackupConfig after)
    {
        var list = new List<string>();
        var oldJobs = before.Jobs.ToDictionary(j => j.Id, StringComparer.Ordinal);
        var newJobs = after.Jobs.ToDictionary(j => j.Id, StringComparer.Ordinal);
        foreach (var j in after.Jobs)
        {
            if (!oldJobs.TryGetValue(j.Id, out var o)) { list.Add($"job '{j.Id}' added"); continue; }
            if (o.Enabled && !j.Enabled) list.Add($"job '{j.Id}' DISABLED");
            else if (!o.Enabled && j.Enabled) list.Add($"job '{j.Id}' enabled");
            var oj = Strip(o);
            var nj = Strip(j);
            if (oj != nj) list.Add($"job '{j.Id}' changed");
        }
        foreach (var j in before.Jobs)
            if (!newJobs.ContainsKey(j.Id)) list.Add($"job '{j.Id}' removed");

        Compare(list, "targets", before.Targets, after.Targets, t => t.Id);
        Compare(list, "restic repository", before.ResticRepos, after.ResticRepos, r => r.Id);
        if (Json(before.Notifications) != Json(after.Notifications)) list.Add("notifications changed");
        if (Json(before.General) != Json(after.General)) list.Add("general settings changed");
        if (before.InstanceName != after.InstanceName) list.Add($"instance name '{before.InstanceName}' → '{after.InstanceName}'");
        if (before.Language != after.Language) list.Add($"language {before.Language} → {after.Language}");
        return list;
    }

    private static string Strip(JobConfig j)
    {
        var c = ConfigJson.Parse(ConfigJson.Serialize(new BackupConfig { InstanceName = "x", Jobs = [j] })).Jobs[0];
        c.Enabled = true;
        return Json(c);
    }

    private static void Compare<T>(List<string> list, string what, List<T> before, List<T> after, Func<T, string> id)
    {
        var o = before.ToDictionary(id, StringComparer.Ordinal);
        var n = after.ToDictionary(id, StringComparer.Ordinal);
        foreach (var (k, v) in n)
        {
            if (!o.TryGetValue(k, out var ov)) list.Add($"{what} '{k}' added");
            else if (Json(ov!) != Json(v!)) list.Add($"{what} '{k}' changed");
        }
        foreach (var k in o.Keys)
            if (!n.ContainsKey(k)) list.Add($"{what} '{k}' removed");
    }

    private static string Json<T>(T value) => System.Text.Json.JsonSerializer.Serialize(value, ConfigDiffJson.Options);
}

internal static class ConfigDiffJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        TypeInfoResolver = System.Text.Json.Serialization.Metadata.JsonTypeInfoResolver.Combine(ConfigJsonContext.Default),
    };
}
