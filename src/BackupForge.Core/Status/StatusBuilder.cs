using BackupForge.Core.Abstractions;
using BackupForge.Core.Config;
using BackupForge.Core.Execution;
using BackupForge.Core.History;
using BackupForge.Core.Scheduling;

namespace BackupForge.Core.Status;

/// <summary>Optional data sources the service plugs in (target probing, Versions folder info, SQL / restic state).</summary>
public sealed class StatusProviders
{
    public Func<TargetConfig, TargetStatus?>? Target { get; init; }
    public Func<JobConfig, VersionsStatus?>? Versions { get; init; }
    public Func<JobConfig, SqlStatus?>? Sql { get; init; }
    public Func<JobConfig, ResticJobStatus?>? ResticJob { get; init; }
    public Func<ResticRepoConfig, RepoStatus?>? Repo { get; init; }
    public Func<NotificationStatus>? Notifications { get; init; }
}

public sealed class StatusInputs
{
    public required BackupConfig Config { get; init; }
    public required HistoryStore History { get; init; }
    public required IClock Clock { get; init; }
    public required DateTimeOffset ServiceStarted { get; init; }
    public string AppVersion { get; init; } = "";
    public string Hostname { get; init; } = Environment.MachineName;
    public bool ConfigOk { get; init; } = true;
    public string? ConfigError { get; init; }
    public DateTimeOffset? ConfigChanged { get; init; }
    public Func<string, DateTimeOffset?> NextRun { get; init; } = _ => null;
    public IReadOnlyList<ActiveRunInfo> Running { get; init; } = [];
    public Func<string, DateTimeOffset?> DisabledSince { get; init; } = _ => null;
    public StatusProviders Providers { get; init; } = new();
}

/// <summary>Builds the status document (STATUS-CONTRACT.md) from config, history and live state.</summary>
public static class StatusBuilder
{
    public const int MaxErrors = 10;
    public const int MaxErrorLength = 300;

    public static StatusDocument Build(StatusInputs inp)
    {
        var now = inp.Clock.UtcNow;
        DateTimeOffset L(DateTimeOffset t) => inp.Clock.ToLocal(t);
        DateTimeOffset? LN(DateTimeOffset? t) => t is null ? null : inp.Clock.ToLocal(t.Value);
        var cfg = inp.Config;

        var doc = new StatusDocument
        {
            AppVersion = inp.AppVersion,
            InstanceName = cfg.InstanceName,
            Hostname = inp.Hostname,
            Service = new ServiceStatus
            {
                Started = L(inp.ServiceStarted),
                Heartbeat = L(now),
                ConfigOk = inp.ConfigOk,
                ConfigError = Truncate(inp.ConfigError, 1000),
                ConfigChanged = LN(inp.ConfigChanged),
            },
        };

        foreach (var job in cfg.Jobs)
            doc.Jobs.Add(BuildJob(inp, job, now, L, LN));

        foreach (var repo in cfg.ResticRepos)
        {
            var rs = inp.Providers.Repo?.Invoke(repo) ?? new RepoStatus();
            rs.Id = repo.Id;
            rs.LocationDisplay = LocationDisplay.Of(repo.Location);
            rs.AppendOnlyServer = repo.AppendOnlyServer;
            var maint = MaintenanceIds.Repo(repo.Id);
            var fp = inp.History.LastRun(maint, RunKinds.ForgetPrune);
            if (fp is not null) { rs.LastForgetPrune = L(fp.StartedAt); rs.LastForgetPruneResult = fp.Result; }
            var ck = inp.History.LastRun(maint, RunKinds.Check);
            if (ck is not null)
            {
                rs.LastCheck = L(ck.StartedAt);
                rs.LastCheckResult = ck.Result;
                rs.LastCheckReadPct ??= TimeSpec.TryParsePercent(repo.Maintenance.CheckReadDataSubset, out var p) ? p : null;
            }
            rs.LastForgetPrune = LN(rs.LastForgetPrune);
            doc.Repos.Add(rs);
        }

        foreach (var t in cfg.Targets)
        {
            var ts = inp.Providers.Target?.Invoke(t) ?? new TargetStatus { Online = false, Error = null };
            ts.Id = t.Id;
            ts.Type = t.Type;
            ts.Path = t.Path;
            ts.MinFreeGb = t.MinFreeGb;
            ts.Checked = LN(ts.Checked);
            doc.Targets.Add(ts);
        }

        doc.Notifications = inp.Providers.Notifications?.Invoke() ?? new NotificationStatus { EmailEnabled = cfg.Notifications.Email.Enabled };
        doc.Notifications.LastSent = LN(doc.Notifications.LastSent);
        return doc;
    }

    private static JobStatus BuildJob(StatusInputs inp, JobConfig job, DateTimeOffset now, Func<DateTimeOffset, DateTimeOffset> L, Func<DateTimeOffset?, DateTimeOffset?> LN)
    {
        var cfg = inp.Config;
        var h = inp.History;
        var expected = ExpectedInterval.For(cfg, job);
        var js = new JobStatus
        {
            Id = job.Id,
            Name = job.Name,
            Type = job.Type,
            Enabled = job.Enabled,
            DisabledSince = job.Enabled ? null : LN(inp.DisabledSince(job.Id)),
            ScheduleText = ScheduleText.Describe(cfg, job),
            ExpectedIntervalH = expected,
            NextRun = job.Enabled ? LN(inp.NextRun(job.Id)) : null,
            Chain = job.OnSuccess.Count > 0 ? new ChainStatus { OnSuccess = [.. job.OnSuccess] } : null,
        };

        var running = inp.Running.FirstOrDefault(r => r.JobId == job.Id && r.Kind == RunKinds.Backup);
        if (running is not null) js.Running = new RunningStatus { Since = L(running.Since), Phase = running.Phase, Percent = running.Percent };

        var last = h.LastRun(job.Id);
        if (last is not null)
        {
            var lr = new LastRunStatus
            {
                RunId = last.Id,
                Trigger = last.Trigger,
                Start = L(last.StartedAt),
                End = LN(last.EndedAt),
                Result = last.Result ?? RunResults.Interrupted,
                Summary = last.Summary,
                Bytes = job.Type == JobTypes.Files ? last.BytesTransferred : Math.Max(last.BytesTransferred, last.BytesProcessed),
                Errors = [.. h.GetItems(last.Id).Where(i => i.Level is ItemLevels.Error or ItemLevels.Warning)
                    .Take(MaxErrors).Select(i => Truncate(i.Path is null ? i.Message : $"{i.Path}: {i.Message}", MaxErrorLength)!)],
            };
            if (job.Type == JobTypes.Files)
            {
                lr.FilesCopied = last.FilesCopied;
                lr.FilesVersioned = last.FilesVersioned;
                lr.FilesDeleted = last.FilesDeleted;
                lr.FilesSkipped = last.FilesSkipped;
            }
            js.LastRun = lr;
        }

        var lastOk = h.LastRunWithResult(job.Id, RunKinds.Backup, RunResults.Ok, RunResults.Warning);
        var lastNotOk = h.LastRunWithResult(job.Id, RunKinds.Backup, RunResults.Failed, RunResults.Interrupted);
        js.LastOk = LN(lastOk?.StartedAt);
        js.LastNotOk = LN(lastNotOk?.StartedAt);

        var day = h.RunsSince(now.AddHours(-24), job.Id).Where(r => r.Kind == RunKinds.Backup && r.Result is not null and not RunResults.Skipped).ToList();
        js.Runs24h = day.Count;
        js.NotOk24h = day.Count(r => RunResults.IsNotOk(r.Result));
        js.Stale = Watchdog.IsStale(job, expected, lastOk?.StartedAt, h.GetTime("seen:job:" + job.Id), running is not null, now);

        if (job.Type == JobTypes.Files)
        {
            var target = job.Target is null ? null : cfg.FindTarget(job.Target.TargetId);
            if (target is not null && job.Target is not null)
            {
                var ts = inp.Providers.Target?.Invoke(target);
                js.Target = new JobTargetStatus
                {
                    Path = JobPaths.MirrorPath(target, job.Target),
                    Online = ts?.Online ?? false,
                    FreeGb = ts?.FreeGb,
                    SizeGb = ts?.SizeGb,
                    MinFreeGb = target.MinFreeGb,
                };
                if (job.Files is { Versioning.Enabled: true })
                {
                    var vs = inp.Providers.Versions?.Invoke(job) ?? new VersionsStatus();
                    vs.Path = JobPaths.VersionsPath(target, job.Target, job.Files.Versioning) ?? "";
                    vs.KeepDays = job.Files.Versioning.KeepDays;
                    vs.Oldest = LN(vs.Oldest);
                    js.Versions = vs;
                }
            }
            js.DeleteGuard = new DeleteGuardStatus
            {
                Tripped = h.GetValue(DeleteGuardState.TrippedKey(job.Id)) is not null,
                PendingApproval = h.GetValue(DeleteGuardState.ApprovedKey(job.Id)) is not null,
            };
        }
        else if (job.Type == JobTypes.Sql)
        {
            js.Sql = inp.Providers.Sql?.Invoke(job) ?? new SqlStatus { Instance = job.Sql?.Instance ?? "" };
            foreach (var d in js.Sql.Dbs)
            {
                d.LastOk = LN(d.LastOk);
                d.LastFullAny = LN(d.LastFullAny);
                d.LastCheckdb = LN(d.LastCheckdb);
                d.Error = Truncate(d.Error, MaxErrorLength);
            }
        }
        else if (job.Type == JobTypes.Restic && job.Restic is not null)
        {
            var repo = cfg.FindRepo(job.Restic.RepoId);
            js.Restic = inp.Providers.ResticJob?.Invoke(job) ?? new ResticJobStatus();
            js.Restic.RepoId = job.Restic.RepoId;
            js.Restic.LocationDisplay = repo is null ? "" : LocationDisplay.Of(repo.Location);
        }
        return js;
    }

    public static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..(max - 1)] + "…";
}

/// <summary>Keys of the delete-guard state in history kv (SPEC 6.5).</summary>
public static class DeleteGuardState
{
    public static string TrippedKey(string jobId) => "delete_guard_tripped:" + jobId;
    public static string ApprovedKey(string jobId) => "delete_guard_approved:" + jobId;
}

/// <summary>Internal watchdog rule (SPEC 10.2).</summary>
public static class Watchdog
{
    public static TimeSpan Threshold(double expectedH) => TimeSpan.FromHours(expectedH * 1.5 + 1);

    public static bool IsStale(JobConfig job, double? expectedH, DateTimeOffset? lastOk, DateTimeOffset? firstSeen, bool running, DateTimeOffset now)
    {
        if (!job.Enabled || expectedH is null || running) return false;
        var reference = lastOk ?? firstSeen;
        return reference is not null && now - reference.Value > Threshold(expectedH.Value);
    }
}

/// <summary>Repository location without credentials (contract: location_display must not contain secrets).</summary>
public static class LocationDisplay
{
    public static string Of(string location)
    {
        if (string.IsNullOrEmpty(location)) return location;
        // sftp:user@host:/path → sftp:host:/path
        if (location.StartsWith("sftp:", StringComparison.OrdinalIgnoreCase) && !location.StartsWith("sftp://", StringComparison.OrdinalIgnoreCase))
        {
            var rest = location[5..];
            var at = rest.IndexOf('@');
            var colon = rest.IndexOf(':');
            if (at >= 0 && (colon < 0 || at < colon)) rest = rest[(at + 1)..];
            return "sftp:" + rest;
        }
        // scheme://user:pw@host/… (rest:, s3:, sftp://) → scheme://host/…
        var idx = location.IndexOf("://", StringComparison.Ordinal);
        if (idx >= 0)
        {
            var start = idx + 3;
            var slash = location.IndexOf('/', start);
            var authority = slash < 0 ? location[start..] : location[start..slash];
            var at = authority.LastIndexOf('@');
            if (at >= 0) return location[..start] + authority[(at + 1)..] + (slash < 0 ? "" : location[slash..]);
        }
        return location;
    }
}
