using System.Globalization;
using System.Text.RegularExpressions;
using BackupForge.Core.Abstractions;
using BackupForge.Core.Resources;
using BackupForge.Core.Scheduling;

namespace BackupForge.Core.Config;

/// <summary>A field-level validation error. <see cref="Path"/> is a JSON path like <c>jobs[2].target.target_id</c>.</summary>
public sealed record ValidationError(string Path, string Code, params object?[] Args)
{
    public string Message(CultureInfo culture) => Texts.Format(culture, Code, Args);
    public override string ToString() => $"{Path}: {Message(Texts.English)}";
}

public sealed class ValidationResult
{
    public List<ValidationError> Errors { get; } = [];
    public bool IsValid => Errors.Count == 0;
    public bool Has(string code) => Errors.Any(e => e.Code == code);
    public bool Has(string code, string path) => Errors.Any(e => e.Code == code && e.Path == path);
    public override string ToString() => string.Join(Environment.NewLine, Errors);
}

/// <summary>Validates config.json semantics (SPEC 4. "Validálás"). Must pass before every save.</summary>
public static partial class ConfigValidator
{
    public const string StagingPrefix = "@staging:";

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$")]
    private static partial Regex IdRegex();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailRegex();

    public static ValidationResult Validate(BackupConfig config, ISecretStore? secrets = null)
    {
        var r = new ValidationResult();
        void Err(string path, string code, params object?[] args) => r.Errors.Add(new ValidationError(path, code, args));

        if (config.Schema != BackupConfig.CurrentSchema) Err("schema", "schema.unsupported", config.Schema, BackupConfig.CurrentSchema);
        if (string.IsNullOrWhiteSpace(config.InstanceName)) Err("instance_name", "required");
        OneOf(r, "language", config.Language, ["hu", "en"]);
        Range(r, "general.max_parallel_jobs", config.General.MaxParallelJobs, 1, 16);
        Range(r, "general.history_retention_days", config.General.HistoryRetentionDays, 7, 3650);
        Range(r, "general.log_retention_days", config.General.LogRetentionDays, 1, 3650);

        void Secret(string path, string? reference, bool required)
        {
            if (string.IsNullOrWhiteSpace(reference))
            {
                if (required) Err(path, "required");
                return;
            }
            if (secrets is not null && !secrets.Has(reference)) Err(path, "ref.secret", reference);
        }

        // --- targets
        Ids(r, "targets", config.Targets.Select(t => t.Id).ToList());
        for (var i = 0; i < config.Targets.Count; i++)
        {
            var t = config.Targets[i];
            var p = $"targets[{i}]";
            OneOf(r, $"{p}.type", t.Type, TargetTypes.All);
            if (string.IsNullOrWhiteSpace(t.Path)) Err($"{p}.path", "required");
            else if (!PathUtil.IsAbsolute(t.Path)) Err($"{p}.path", "path.not_absolute", t.Path);
            else if (t.Type == TargetTypes.Smb && !PathUtil.IsUnc(t.Path)) Err($"{p}.path", "path.smb_unc", t.Path);
            if (t.Type == TargetTypes.Smb && !string.IsNullOrEmpty(t.Username)) Secret($"{p}.password_ref", t.PasswordRef, required: true);
            else if (t.PasswordRef is not null) Secret($"{p}.password_ref", t.PasswordRef, required: false);
            if (t.MinFreeGb is < 0) Err($"{p}.min_free_gb", "range", t.MinFreeGb, 0, "∞");
        }

        // --- restic repos
        Ids(r, "restic_repos", config.ResticRepos.Select(x => x.Id).ToList());
        for (var i = 0; i < config.ResticRepos.Count; i++)
        {
            var repo = config.ResticRepos[i];
            var p = $"restic_repos[{i}]";
            if (string.IsNullOrWhiteSpace(repo.Location)) Err($"{p}.location", "required");
            Secret($"{p}.password_ref", repo.PasswordRef, required: true);
            foreach (var (env, reference) in repo.EnvRefs) Secret($"{p}.env_refs.{env}", reference, required: true);
            Recurrence(r, $"{p}.maintenance.forget_prune", repo.Maintenance.ForgetPrune);
            Recurrence(r, $"{p}.maintenance.check", repo.Maintenance.Check);
            if (!TimeSpec.TryParsePercent(repo.Maintenance.CheckReadDataSubset, out _))
                Err($"{p}.maintenance.check_read_data_subset", "percent.invalid", repo.Maintenance.CheckReadDataSubset);
            var ret = repo.Retention;
            foreach (var (name, v) in new[] { ("keep_last", ret.KeepLast), ("keep_daily", ret.KeepDaily), ("keep_weekly", ret.KeepWeekly), ("keep_monthly", ret.KeepMonthly), ("keep_yearly", ret.KeepYearly) })
                if (v is < 0 or > 10000) Err($"{p}.retention.{name}", "range", v, 0, 10000);
        }

        // --- jobs
        Ids(r, "jobs", config.Jobs.Select(j => j.Id).ToList());
        var mirrorTargets = new List<(string Path, string JobId)>();
        for (var i = 0; i < config.Jobs.Count; i++)
        {
            var j = config.Jobs[i];
            var p = $"jobs[{i}]";
            if (string.IsNullOrWhiteSpace(j.Name)) Err($"{p}.name", "required");
            if (!OneOf(r, $"{p}.type", j.Type, JobTypes.All)) continue;
            ValidateSchedule(r, $"{p}.schedule", j.Schedule);
            if (j.ExpectedIntervalH is <= 0 or > 24 * 400) Err($"{p}.expected_interval_h", "range", j.ExpectedIntervalH, 0, 9600);
            if (j.TimeoutMin is < 1 or > 60 * 24 * 7) Err($"{p}.timeout_min", "range", j.TimeoutMin, 1, 10080);

            for (var k = 0; k < j.OnSuccess.Count; k++)
                if (config.FindJob(j.OnSuccess[k]) is null) Err($"{p}.on_success[{k}]", "ref.job", j.OnSuccess[k]);

            // sources
            if (j.Type is JobTypes.Files or JobTypes.Restic)
            {
                if (j.Sources.Count == 0) Err($"{p}.sources", "sources.empty");
                for (var k = 0; k < j.Sources.Count; k++)
                {
                    var src = j.Sources[k].Path;
                    var sp = $"{p}.sources[{k}].path";
                    if (string.IsNullOrWhiteSpace(src)) { Err(sp, "required"); continue; }
                    if (src.StartsWith(StagingPrefix, StringComparison.Ordinal))
                    {
                        var refId = src[StagingPrefix.Length..];
                        var refJob = config.FindJob(refId);
                        if (refJob is null) Err(sp, "ref.job", refId);
                        else if (refJob.Type != JobTypes.Sql) Err(sp, "staging.not_sql", refId);
                    }
                    else if (!PathUtil.IsAbsolute(src)) Err(sp, "path.not_absolute", src);
                }
            }

            switch (j.Type)
            {
                case JobTypes.Files:
                    ValidateFilesJob(r, config, j, p, mirrorTargets);
                    break;
                case JobTypes.Restic:
                    if (j.Restic is null) { Err($"{p}.restic", "block.required", "restic", j.Type); break; }
                    if (string.IsNullOrWhiteSpace(j.Restic.RepoId)) Err($"{p}.restic.repo_id", "required");
                    else if (config.FindRepo(j.Restic.RepoId) is null) Err($"{p}.restic.repo_id", "ref.repo", j.Restic.RepoId);
                    OneOf(r, $"{p}.restic.vss", j.Restic.Vss, VssModes.All);
                    break;
                case JobTypes.Sql:
                    if (j.Sql is null) { Err($"{p}.sql", "block.required", "sql", j.Type); break; }
                    if (string.IsNullOrWhiteSpace(j.Sql.Instance)) Err($"{p}.sql.instance", "required");
                    OneOf(r, $"{p}.sql.auth", j.Sql.Auth, SqlAuthModes.All);
                    if (j.Sql.Auth == SqlAuthModes.Sql)
                    {
                        if (string.IsNullOrWhiteSpace(j.Sql.Username)) Err($"{p}.sql.username", "required");
                        Secret($"{p}.sql.login_ref", j.Sql.LoginRef, required: true);
                    }
                    OneOf(r, $"{p}.sql.databases.mode", j.Sql.Databases.Mode, SqlDbModes.All);
                    if (j.Sql.Databases.Mode == SqlDbModes.Selected && j.Sql.Databases.Include.Count == 0)
                        Err($"{p}.sql.databases.include", "sql.selected_empty");
                    OneOf(r, $"{p}.sql.compression", j.Sql.Compression, SqlCompressionModes.All);
                    Range(r, $"{p}.sql.keep_local", j.Sql.KeepLocal, 0, 100);
                    Recurrence(r, $"{p}.sql.checkdb", j.Sql.Checkdb);
                    if (j.Sql.StagingPath is not null && !PathUtil.IsAbsolute(j.Sql.StagingPath))
                        Err($"{p}.sql.staging_path", "path.not_absolute", j.Sql.StagingPath);
                    break;
            }
        }

        // --- chain cycles (each cycle reported once, at the job where it was detected)
        foreach (var cycle in FindCycles(config))
        {
            var idx = config.Jobs.FindIndex(x => x.Id == cycle[0]);
            Err($"jobs[{idx}].on_success", "chain.cycle", string.Join(" → ", cycle));
        }

        // --- notifications
        var mail = config.Notifications.Email;
        if (mail.Enabled)
        {
            if (string.IsNullOrWhiteSpace(mail.SmtpHost)) Err("notifications.email.smtp_host", "required");
            Range(r, "notifications.email.smtp_port", mail.SmtpPort, 1, 65535);
            OneOf(r, "notifications.email.security", mail.Security, SmtpSecurityModes.All);
            if (string.IsNullOrWhiteSpace(mail.From)) Err("notifications.email.from", "required");
            else if (!EmailRegex().IsMatch(mail.From)) Err("notifications.email.from", "email.address_invalid", mail.From);
            if (mail.To.Count == 0) Err("notifications.email.to", "required");
            for (var k = 0; k < mail.To.Count; k++)
                if (!EmailRegex().IsMatch(mail.To[k])) Err($"notifications.email.to[{k}]", "email.address_invalid", mail.To[k]);
            if (!string.IsNullOrEmpty(mail.Username)) Secret("notifications.email.password_ref", mail.PasswordRef, required: true);
            Recurrence(r, "notifications.email.weekly_report", mail.WeeklyReport);
        }
        var hb = config.Notifications.HeartbeatUrl;
        if (!string.IsNullOrWhiteSpace(hb) &&
            !(Uri.TryCreate(hb, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp)))
            Err("notifications.heartbeat_url", "url.invalid", hb);

        return r;
    }

    private static void ValidateFilesJob(ValidationResult r, BackupConfig config, JobConfig j, string p, List<(string Path, string JobId)> mirrorTargets)
    {
        void Err(string path, string code, params object?[] args) => r.Errors.Add(new ValidationError(path, code, args));
        if (j.Files is null) { Err($"{p}.files", "block.required", "files", j.Type); }
        else
        {
            var f = j.Files;
            OneOf(r, $"{p}.files.mode", f.Mode, FilesModes.All);
            OneOf(r, $"{p}.files.vss", f.Vss, VssModes.All);
            OneOf(r, $"{p}.files.compare", f.Compare, CompareModes.All);
            OneOf(r, $"{p}.files.cloud_files", f.CloudFiles, CloudFilesModes.All);
            Range(r, $"{p}.files.delete_guard_pct", f.DeleteGuardPct, 1, 100);
            Range(r, $"{p}.files.delete_guard_min_files", f.DeleteGuardMinFiles, 0, 10_000_000);
            Range(r, $"{p}.files.parallelism", f.Parallelism, 1, 16);
            Range(r, $"{p}.files.versioning.keep_days", f.Versioning.KeepDays, 0, 3650);
            if (f.Versioning.MaxVersionsPerFile is < 1) Err($"{p}.files.versioning.max_versions_per_file", "range", f.Versioning.MaxVersionsPerFile, 1, "∞");
            if (f.ThrottleMbps is <= 0) Err($"{p}.files.throttle_mbps", "range", f.ThrottleMbps, 0, "∞");
        }

        if (j.Target is null) { Err($"{p}.target", "block.required", "target", j.Type); return; }
        var target = string.IsNullOrWhiteSpace(j.Target.TargetId) ? null : config.FindTarget(j.Target.TargetId);
        if (string.IsNullOrWhiteSpace(j.Target.TargetId)) { Err($"{p}.target.target_id", "required"); return; }
        if (target is null) { Err($"{p}.target.target_id", "ref.target", j.Target.TargetId); return; }
        if (string.IsNullOrWhiteSpace(target.Path)) return;

        var mirror = JobPaths.MirrorPath(target, j.Target);
        var dup = mirrorTargets.FirstOrDefault(m => PathUtil.EqualsPath(m.Path, mirror));
        if (dup.JobId is not null) Err($"{p}.target", "target.shared", dup.JobId);
        mirrorTargets.Add((mirror, j.Id));

        var leaves = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var k = 0; k < j.Sources.Count; k++)
        {
            var src = j.Sources[k].Path;
            if (string.IsNullOrWhiteSpace(src)) continue;
            if (j.Sources.Count > 1)
            {
                var leaf = PathUtil.Normalize(src).TrimEnd('\\');
                leaf = leaf[(leaf.LastIndexOf('\\') + 1)..].Replace(":", "", StringComparison.Ordinal);
                if (src.StartsWith(StagingPrefix, StringComparison.Ordinal)) leaf = src[StagingPrefix.Length..];
                if (!leaves.Add(leaf)) Err($"{p}.sources[{k}].path", "sources.leaf_duplicate", leaf);
            }
            if (src.StartsWith(StagingPrefix, StringComparison.Ordinal)) continue;
            if (PathUtil.Overlaps(src, mirror)) Err($"{p}.sources[{k}].path", "path.nested", src, mirror);
        }

        if (j.Files is { Versioning.Enabled: true } files)
        {
            if (string.IsNullOrWhiteSpace(j.Target.Subdir) && string.IsNullOrWhiteSpace(files.Versioning.Folder))
            {
                Err($"{p}.files.versioning.folder", "versioning.folder_required");
                return;
            }
            var versions = JobPaths.VersionsPath(target, j.Target, files.Versioning);
            if (versions is not null &&
                (PathUtil.IsSameOrUnder(versions, mirror) || j.Sources.Any(s => !s.Path.StartsWith(StagingPrefix, StringComparison.Ordinal) && PathUtil.Overlaps(versions, s.Path))))
                Err($"{p}.files.versioning.folder", "path.versions_inside", versions);
        }
    }

    private static void ValidateSchedule(ValidationResult r, string p, ScheduleConfig s)
    {
        void Err(string path, string code, params object?[] args) => r.Errors.Add(new ValidationError(path, code, args));
        if (!OneOf(r, $"{p}.kind", s.Kind, ScheduleKinds.All)) return;
        switch (s.Kind)
        {
            case ScheduleKinds.Interval:
                if (s.EveryMin is null) Err($"{p}.every_min", "required");
                else Range(r, $"{p}.every_min", s.EveryMin.Value, 5, 60 * 24 * 31);
                if (s.Start is not null) Time(r, $"{p}.start", s.Start);
                if (s.Window is not null)
                {
                    Time(r, $"{p}.window.from", s.Window.From);
                    Time(r, $"{p}.window.to", s.Window.To);
                }
                break;
            case ScheduleKinds.Daily:
                if (s.Times.Count == 0) Err($"{p}.times", "required");
                for (var i = 0; i < s.Times.Count; i++) Time(r, $"{p}.times[{i}]", s.Times[i]);
                for (var i = 0; i < s.Days.Count; i++)
                    if (!TimeSpec.TryParseDay(s.Days[i], out _)) Err($"{p}.days[{i}]", "day.invalid", s.Days[i]);
                break;
            case ScheduleKinds.Weekly:
                if (!TimeSpec.TryParseDay(s.Day, out _)) Err($"{p}.day", s.Day is null ? "required" : "day.invalid", s.Day);
                Time(r, $"{p}.time", s.Time);
                break;
            case ScheduleKinds.Monthly:
                if (s.DayOfMonth is null) Err($"{p}.day_of_month", "required");
                else if (!TimeSpec.IsValidDayOfMonth(s.DayOfMonth)) Err($"{p}.day_of_month", "value.invalid", s.DayOfMonth, "1–28, last");
                Time(r, $"{p}.time", s.Time);
                break;
        }
    }

    private static void Time(ValidationResult r, string path, string? value)
    {
        if (value is null) r.Errors.Add(new ValidationError(path, "required"));
        else if (!TimeSpec.TryParseHm(value, out _)) r.Errors.Add(new ValidationError(path, "time.invalid", value));
    }

    private static void Recurrence(ValidationResult r, string path, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return; // null = disabled
        if (!TimeSpec.TryParseRecurrence(value, out _)) r.Errors.Add(new ValidationError(path, "recurrence.invalid", value));
    }

    private static bool OneOf(ValidationResult r, string path, string? value, string[] allowed)
    {
        if (value is not null && allowed.Contains(value, StringComparer.Ordinal)) return true;
        r.Errors.Add(value is null ? new ValidationError(path, "required") : new ValidationError(path, "value.invalid", value, string.Join(", ", allowed)));
        return false;
    }

    private static void Range(ValidationResult r, string path, long value, long min, long max)
    {
        if (value < min || value > max) r.Errors.Add(new ValidationError(path, "range", value, min, max));
    }

    private static void Ids(ValidationResult r, string listPath, List<string> ids)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < ids.Count; i++)
        {
            var id = ids[i];
            var path = $"{listPath}[{i}].id";
            if (string.IsNullOrEmpty(id)) r.Errors.Add(new ValidationError(path, "required"));
            else if (!IdRegex().IsMatch(id)) r.Errors.Add(new ValidationError(path, "id.invalid", id));
            else if (!seen.Add(id)) r.Errors.Add(new ValidationError(path, "id.duplicate", id));
        }
    }

    /// <summary>Returns each distinct cycle in the on_success graph as a closed list (a → b → a).</summary>
    public static List<List<string>> FindCycles(BackupConfig config)
    {
        var cycles = new List<List<string>>();
        var seenCycles = new HashSet<string>(StringComparer.Ordinal);
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 0 new, 1 on stack, 2 done
        var stack = new List<string>();

        void Visit(string id)
        {
            state[id] = 1;
            stack.Add(id);
            var job = config.FindJob(id);
            foreach (var next in job?.OnSuccess ?? [])
            {
                if (config.FindJob(next) is null) continue;
                var st = state.GetValueOrDefault(next);
                if (st == 1)
                {
                    var start = stack.IndexOf(next);
                    var cycle = stack.Skip(start).Append(next).ToList();
                    var key = string.Join(",", cycle.Take(cycle.Count - 1).Order(StringComparer.Ordinal));
                    if (seenCycles.Add(key)) cycles.Add(cycle);
                }
                else if (st == 0) Visit(next);
            }
            stack.RemoveAt(stack.Count - 1);
            state[id] = 2;
        }

        foreach (var j in config.Jobs)
            if (!string.IsNullOrEmpty(j.Id) && state.GetValueOrDefault(j.Id) == 0) Visit(j.Id);
        return cycles;
    }
}
