using BackupForge.Core.Abstractions;
using BackupForge.Core.Config;
using BackupForge.Core.History;
using BackupForge.Core.Scheduling;
using Microsoft.Extensions.Logging;

namespace BackupForge.Core.Execution;

/// <summary>One scheduled source of work (a job, a repository maintenance task or an SQL CHECKDB).</summary>
public sealed record ScheduledEntry(string Key, string JobId, string Kind, ScheduleConfig Schedule, bool CatchUp);

/// <summary>
/// Turns schedules into work requests (SPEC 4./5.): due runs, catch-up of missed runs after start (once,
/// 2 minutes after the start) and the next due time of every job for the status.
/// </summary>
public sealed class JobScheduler
{
    public static readonly TimeSpan DefaultCatchUpDelay = TimeSpan.FromMinutes(2);

    private readonly IClock _clock;
    private readonly JobCoordinator _coordinator;
    private readonly HistoryStore _history;
    private readonly ILogger _log;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (ScheduledEntry Entry, DateTimeOffset? Next)> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _catchUp = new(StringComparer.Ordinal);

    public JobScheduler(IClock clock, JobCoordinator coordinator, HistoryStore history, ILogger logger)
    {
        _clock = clock;
        _coordinator = coordinator;
        _history = history;
        _log = logger;
    }

    public TimeSpan CatchUpDelay { get; init; } = DefaultCatchUpDelay;

    public static IEnumerable<ScheduledEntry> EntriesFor(BackupConfig config)
    {
        foreach (var j in config.Jobs)
        {
            if (!j.Enabled) continue;
            if (ScheduleCalculator.IsTimeBased(j.Schedule))
                yield return new ScheduledEntry("job:" + j.Id, j.Id, RunKinds.Backup, j.Schedule, j.CatchUp);
            if (j.Type == JobTypes.Sql && j.Sql?.Checkdb is { } cd && TimeSpec.TryParseRecurrence(cd, out var cds))
                yield return new ScheduledEntry("checkdb:" + j.Id, j.Id, RunKinds.Checkdb, cds, true);
        }
        foreach (var r in config.ResticRepos)
        {
            // only repositories that are actually used by an enabled job are maintained
            if (!config.Jobs.Any(j => j.Enabled && j.Restic?.RepoId == r.Id)) continue;
            if (!r.AppendOnlyServer && TimeSpec.TryParseRecurrence(r.Maintenance.ForgetPrune, out var fp))
                yield return new ScheduledEntry("forget_prune:" + r.Id, MaintenanceIds.Repo(r.Id), RunKinds.ForgetPrune, fp, true);
            if (TimeSpec.TryParseRecurrence(r.Maintenance.Check, out var ck))
                yield return new ScheduledEntry("check:" + r.Id, MaintenanceIds.Repo(r.Id), RunKinds.Check, ck, true);
        }
    }

    /// <summary>(Re)builds the timetable. On <paramref name="startup"/> missed runs are queued for catch-up.</summary>
    public void Load(BackupConfig config, bool startup)
    {
        var now = _clock.UtcNow;
        var zone = _clock.LocalZone;
        lock (_lock)
        {
            _entries.Clear();
            if (!startup) _catchUp.Clear();
            foreach (var e in EntriesFor(config))
            {
                var seenKey = "seen:" + e.Key;
                var seen = _history.GetTime(seenKey);
                if (seen is null) _history.SetTime(seenKey, now);
                _entries[e.Key] = (e, ScheduleCalculator.Next(e.Schedule, now, zone));

                if (!startup || !e.CatchUp || seen is null) continue;
                var prev = ScheduleCalculator.Previous(e.Schedule, now, zone);
                if (prev is null || prev <= seen) continue;
                var lastStart = _history.LastStart(e.JobId, e.Kind);
                if (lastStart is null || lastStart < prev)
                {
                    _catchUp[e.Key] = now + CatchUpDelay;
                    _log.LogInformation("{Key}: missed run at {Due:o}, catch-up in {Delay}", e.Key, prev, CatchUpDelay);
                }
            }
        }
    }

    /// <summary>Queues everything that is due. Call regularly (the service does it every few seconds).</summary>
    public void Tick()
    {
        var now = _clock.UtcNow;
        var zone = _clock.LocalZone;
        var due = new List<(ScheduledEntry Entry, string Trigger)>();
        lock (_lock)
        {
            foreach (var key in _catchUp.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList())
            {
                _catchUp.Remove(key);
                if (_entries.TryGetValue(key, out var ce)) due.Add((ce.Entry, RunTriggers.CatchUp));
            }
            foreach (var key in _entries.Keys.ToList())
            {
                var (entry, next) = _entries[key];
                if (next is null || next > now) continue;
                _entries[key] = (entry, ScheduleCalculator.Next(entry.Schedule, now, zone));
                if (due.Any(d => d.Entry.Key == key)) continue; // catch-up and regular run in the same tick → once
                due.Add((entry, RunTriggers.Schedule));
            }
        }
        foreach (var (entry, trigger) in due)
            _coordinator.Enqueue(new WorkRequest { JobId = entry.JobId, Kind = entry.Kind, Trigger = trigger });
    }

    public DateTimeOffset? NextRun(string jobId, string kind = RunKinds.Backup)
    {
        lock (_lock)
        {
            var key = kind == RunKinds.Backup ? "job:" + jobId : kind + ":" + (MaintenanceIds.RepoIdOf(jobId) ?? jobId);
            if (_catchUp.TryGetValue(key, out var c)) return c;
            return _entries.TryGetValue(key, out var e) ? e.Next : null;
        }
    }

    /// <summary>Earliest pending due time (lets the service sleep until then).</summary>
    public DateTimeOffset? EarliestDue()
    {
        lock (_lock)
        {
            var times = _entries.Values.Select(v => v.Next).Where(n => n is not null).Select(n => n!.Value).Concat(_catchUp.Values).ToList();
            return times.Count == 0 ? null : times.Min();
        }
    }
}
