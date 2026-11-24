using BackupForge.Core.Abstractions;
using BackupForge.Core.Config;
using BackupForge.Core.History;
using BackupForge.Core.Logging;
using Microsoft.Extensions.Logging;

namespace BackupForge.Core.Execution;

public enum CoordinatorEventType { Started, Progress, Finished, Skipped }

public sealed record CoordinatorEvent(CoordinatorEventType Type, string JobId, string Kind, long? RunId, RunProgress? Progress = null, RunRecord? Record = null);

public sealed record EnqueueResult(bool Accepted, string? Reason, Task<RunRecord?> Completion)
{
    public static EnqueueResult Rejected(string reason) => new(false, reason, Task.FromResult<RunRecord?>(null));
}

public sealed record ActiveRunInfo(string JobId, string Kind, long RunId, string Trigger, DateTimeOffset Since, string Phase, double? Percent, string? ChainRunId);

/// <summary>
/// The work queue (SPEC 5.): global parallelism limit, resource locks, per-job exclusivity, timeouts,
/// cancellation and chains. Runs are executed by <see cref="IJobEngine"/>s on the thread pool.
/// </summary>
public sealed class JobCoordinator : IAsyncDisposable
{
    public static readonly TimeSpan DefaultShutdownGrace = TimeSpan.FromSeconds(60);

    private readonly IClock _clock;
    private readonly HistoryStore _history;
    private readonly IReadOnlyList<IJobEngine> _engines;
    private readonly ISecretStore _secrets;
    private readonly DataPaths _paths;
    private readonly SecretMasker _masker;
    private readonly ILogger _log;
    private readonly Lock _lock = new();
    private readonly List<Pending> _pending = [];
    private readonly Dictionary<string, Active> _active = new(StringComparer.Ordinal);
    private readonly HashSet<string> _heldLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _shutdown = new();
    private BackupConfig _config;
    private bool _stopping;

    public JobCoordinator(BackupConfig config, IClock clock, HistoryStore history, IEnumerable<IJobEngine> engines,
        ISecretStore secrets, DataPaths paths, SecretMasker masker, ILogger logger)
    {
        _config = config;
        _clock = clock;
        _history = history;
        _engines = [.. engines];
        _secrets = secrets;
        _paths = paths;
        _masker = masker;
        _log = logger;
    }

    public event Action<CoordinatorEvent>? Changed;

    /// <summary>Run time limit (SPEC 5.: timeout_min, default per type). Replaceable for tests.</summary>
    public Func<JobConfig?, TimeSpan> TimeoutFor { get; init; } = job => TimeSpan.FromMinutes(job?.EffectiveTimeoutMin ?? 1440);

    public BackupConfig Config { get { lock (_lock) return _config; } }

    public void UpdateConfig(BackupConfig config)
    {
        lock (_lock) _config = config;
        Dispatch();
    }

    public IReadOnlyList<ActiveRunInfo> Running
    {
        get
        {
            lock (_lock)
                return [.. _active.Values.Select(a => new ActiveRunInfo(a.Request.JobId, a.Request.Kind, a.RunId, a.Request.Trigger, a.Since, a.Progress.Phase, a.Progress.Percent, a.ChainRunId))];
        }
    }

    public IReadOnlyList<WorkRequest> Queued { get { lock (_lock) return [.. _pending.Select(p => p.Request)]; } }

    public bool IsBusy(string jobId)
    {
        lock (_lock) return _active.ContainsKey(jobId) || _pending.Any(p => p.Request.JobId == jobId);
    }

    public EnqueueResult Enqueue(WorkRequest request)
    {
        EnqueueResult result;
        lock (_lock)
        {
            if (_stopping) return EnqueueResult.Rejected("service is stopping");
            var isMaintenance = request.JobId.StartsWith(MaintenanceIds.RepoPrefix, StringComparison.Ordinal);
            var job = _config.FindJob(request.JobId);
            if (job is null && !isMaintenance) return EnqueueResult.Rejected($"unknown job '{request.JobId}'");
            var automatic = request.Trigger != RunTriggers.Manual;

            if (_active.ContainsKey(request.JobId) || _pending.Any(p => p.Request.JobId == request.JobId))
            {
                if (!automatic) return EnqueueResult.Rejected("already running or queued");
                RecordSkip(request, "skipped (still running)");
                return EnqueueResult.Rejected("still running");
            }

            if (job is { Enabled: false } && automatic)
            {
                RecordSkip(request, "skipped (disabled)");
                return EnqueueResult.Rejected("disabled");
            }

            var p = new Pending(request, _clock.UtcNow);
            _pending.Add(p);
            result = new EnqueueResult(true, null, p.Done.Task);
        }
        Dispatch();
        return result;
    }

    /// <summary>Cancels a running or queued job. Returns false if it was neither.</summary>
    public bool Cancel(string jobId)
    {
        lock (_lock)
        {
            if (_active.TryGetValue(jobId, out var a))
            {
                a.CancelReason = "cancelled";
                a.Cts.Cancel();
                return true;
            }
            var idx = _pending.FindIndex(p => p.Request.JobId == jobId);
            if (idx < 0) return false;
            var pend = _pending[idx];
            _pending.RemoveAt(idx);
            pend.Done.TrySetResult(null);
            return true;
        }
    }

    /// <summary>Stops accepting work, cancels everything and waits up to <paramref name="grace"/> for running engines.</summary>
    public async Task ShutdownAsync(TimeSpan grace)
    {
        Task[] running;
        lock (_lock)
        {
            if (_stopping && _active.Count == 0) return;
            _stopping = true;
            foreach (var p in _pending) p.Done.TrySetResult(null);
            _pending.Clear();
            foreach (var a in _active.Values) a.CancelReason ??= "service stopping";
            running = [.. _active.Values.Select(a => a.Task)];
        }
        _shutdown.Cancel();
        try
        {
            await Task.WhenAll(running).WaitAsync(grace).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _log.LogWarning("{Count} run(s) did not stop within {Grace}", running.Count(t => !t.IsCompleted), grace);
        }
    }

    public ValueTask DisposeAsync() => new(ShutdownAsync(TimeSpan.FromSeconds(5)));

    // ------------------------------------------------------------------ internals

    private void RecordSkip(WorkRequest request, string summary)
    {
        var id = _history.RecordSkipped(request.JobId, request.Kind, request.Trigger, request.ChainRunId, _clock.UtcNow, summary);
        _log.LogInformation("{Job}: {Summary} (trigger {Trigger})", request.JobId, summary, request.Trigger);
        Raise(new CoordinatorEvent(CoordinatorEventType.Skipped, request.JobId, request.Kind, id, Record: _history.GetRun(id)));
    }

    private void Dispatch()
    {
        var toStart = new List<Active>();
        lock (_lock)
        {
            if (_stopping) return;
            var max = Math.Max(1, _config.General.MaxParallelJobs);
            for (var i = 0; i < _pending.Count && _active.Count + toStart.Count < max;)
            {
                var p = _pending[i];
                var locks = LocksFor(p.Request);
                if (locks.Any(_heldLocks.Contains)) { i++; continue; }
                _pending.RemoveAt(i);
                foreach (var l in locks) _heldLocks.Add(l);
                var job = _config.FindJob(p.Request.JobId);
                var chainId = p.Request.ChainRunId ?? (job is { OnSuccess.Count: > 0 } && !p.Request.Options.DryRun && p.Request.Kind == RunKinds.Backup
                    ? "c-" + Guid.NewGuid().ToString("N")[..10]
                    : null);
                var runId = _history.StartRun(p.Request.JobId, p.Request.Kind, p.Request.Trigger, chainId, _clock.UtcNow);
                var a = new Active(p, runId, chainId, locks, _clock.UtcNow, CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token), _config, job);
                _active[p.Request.JobId] = a;
                toStart.Add(a);
            }
        }
        foreach (var a in toStart)
        {
            Raise(new CoordinatorEvent(CoordinatorEventType.Started, a.Request.JobId, a.Request.Kind, a.RunId));
            a.Task = Task.Run(() => ExecuteAsync(a));
        }
    }

    private string[] LocksFor(WorkRequest request)
    {
        var locks = new List<string>(ResourceLocks.For(_config, request));
        var job = _config.FindJob(request.JobId);
        var engine = FindEngine(job?.Type, request);
        if (engine is not null) locks.AddRange(engine.Locks(_config, request));
        return [.. locks.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private IJobEngine? FindEngine(string? jobType, WorkRequest request)
    {
        var type = jobType ?? MaintenanceIds.JobTypeFor(request.JobId);
        return type is null ? null : _engines.FirstOrDefault(e => e.CanRun(type, request.Kind));
    }

    private async Task ExecuteAsync(Active a)
    {
        var request = a.Request;
        RunOutcome outcome;
        using var timeout = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(a.Cts.Token, timeout.Token);
        FileRunLog? runLog = null;
        try
        {
            runLog = new FileRunLog(_paths.RunLogFile(request.JobId, a.RunId), _clock, _masker);
            runLog.Info($"run {a.RunId}: job '{request.JobId}', kind {request.Kind}, trigger {request.Trigger}" +
                        (request.Options.DryRun ? ", dry-run" : "") + (a.ChainRunId is null ? "" : $", chain {a.ChainRunId}"));
            var engine = FindEngine(a.Job?.Type, request);
            if (engine is null)
            {
                outcome = RunOutcome.Failed($"no engine for '{a.Job?.Type ?? request.JobId}' / {request.Kind} in this version");
            }
            else
            {
                var limit = TimeoutFor(a.Job);
                timeout.CancelAfter(limit);
                var ctx = new JobRunContext
                {
                    Config = a.Config,
                    Job = a.Job,
                    Request = request,
                    RunId = a.RunId,
                    Log = runLog,
                    Progress = new ProgressSink(this, a),
                    Clock = _clock,
                    Secrets = _secrets,
                    Paths = _paths,
                    State = _history,
                    VssSnapshotCreated = (id, set) => _history.AddVssSnapshot(_clock.UtcNow, a.RunId, id, set),
                    VssSnapshotDeleted = id => _history.MarkVssDeleted(id, _clock.UtcNow),
                };
                try
                {
                    outcome = await engine.RunAsync(ctx, linked.Token).ConfigureAwait(false);
                    if (linked.IsCancellationRequested && outcome.Result is RunResults.Ok or RunResults.Warning && !request.Options.DryRun)
                    {
                        // The engine returned normally although it was asked to stop: keep its result, it finished.
                    }
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested && !a.Cts.IsCancellationRequested)
                {
                    outcome = RunOutcome.Failed($"timeout ({limit.TotalMinutes:0.##} min)");
                }
                catch (OperationCanceledException) when (a.Cts.IsCancellationRequested)
                {
                    outcome = new RunOutcome { Result = RunResults.Interrupted, Summary = $"interrupted ({a.CancelReason ?? "cancelled"})" };
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogError(ex, "{Job}: run {RunId} crashed", request.JobId, a.RunId);
            outcome = RunOutcome.Failed("error: " + _masker.Apply(ex.Message));
            runLog?.Error(ex.ToString());
        }

        outcome.Summary = _masker.Apply(outcome.Summary);
        var ended = _clock.UtcNow;
        try
        {
            runLog?.Info($"result: {outcome.Result} — {outcome.Summary}");
            _history.FinishRun(a.RunId, ended, outcome);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogError(ex, "{Job}: could not store the result of run {RunId}", request.JobId, a.RunId);
        }
        finally
        {
            runLog?.Dispose();
        }

        RunRecord? record = null;
        try { record = _history.GetRun(a.RunId); } catch (Exception ex) when (ex is not OutOfMemoryException) { _log.LogError(ex, "history read failed"); }

        lock (_lock)
        {
            _active.Remove(request.JobId);
            foreach (var l in a.Locks) _heldLocks.Remove(l);
        }
        a.Cts.Dispose();
        _log.Log(outcome.Result is RunResults.Failed ? LogLevel.Error : outcome.Result is RunResults.Warning or RunResults.Interrupted ? LogLevel.Warning : LogLevel.Information,
            "{Job}: run {RunId} {Result} — {Summary}", request.JobId, a.RunId, outcome.Result, outcome.Summary);
        Raise(new CoordinatorEvent(CoordinatorEventType.Finished, request.JobId, request.Kind, a.RunId, Record: record));
        a.Pending.Done.TrySetResult(record);

        if (request.Kind == RunKinds.Backup && !request.Options.DryRun && a.Job is { OnSuccess.Count: > 0 })
            ContinueChain(a, outcome.Result);
        Dispatch();
    }

    private void ContinueChain(Active a, string result)
    {
        foreach (var next in a.Job!.OnSuccess)
        {
            var req = new WorkRequest { JobId = next, Trigger = RunTriggers.Chain, ChainRunId = a.ChainRunId };
            if (RunResults.IsSuccess(result))
            {
                Enqueue(req);
            }
            else
            {
                lock (_lock)
                {
                    if (_stopping) return;
                    RecordSkip(req, $"skipped (chain: {a.Request.JobId} {result})");
                }
            }
        }
    }

    private void Raise(CoordinatorEvent e)
    {
        try { Changed?.Invoke(e); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _log.LogWarning(ex, "event handler failed"); }
    }

    private sealed class Pending(WorkRequest request, DateTimeOffset queuedAt)
    {
        public WorkRequest Request { get; } = request;
        public DateTimeOffset QueuedAt { get; } = queuedAt;
        public TaskCompletionSource<RunRecord?> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Active(Pending pending, long runId, string? chainRunId, string[] locks, DateTimeOffset since, CancellationTokenSource cts, BackupConfig config, JobConfig? job)
    {
        public Pending Pending { get; } = pending;
        public WorkRequest Request => Pending.Request;
        public long RunId { get; } = runId;
        public string? ChainRunId { get; } = chainRunId;
        public string[] Locks { get; } = locks;
        public DateTimeOffset Since { get; } = since;
        public CancellationTokenSource Cts { get; } = cts;
        public BackupConfig Config { get; } = config;
        public JobConfig? Job { get; } = job;
        public RunProgress Progress { get; set; } = new(RunPhases.Plan, null);
        public string? CancelReason { get; set; }
        public Task Task { get; set; } = Task.CompletedTask;
    }

    private sealed class ProgressSink(JobCoordinator owner, Active active) : IProgress<RunProgress>
    {
        public void Report(RunProgress value)
        {
            active.Progress = value;
            owner.Raise(new CoordinatorEvent(CoordinatorEventType.Progress, active.Request.JobId, active.Request.Kind, active.RunId, value));
        }
    }
}

/// <summary>Ids of repository-level work (restic forget/prune and check): "repo:&lt;repoId&gt;".</summary>
public static class MaintenanceIds
{
    public const string RepoPrefix = "repo:";
    public static string Repo(string repoId) => RepoPrefix + repoId;
    public static string? RepoIdOf(string jobId) => jobId.StartsWith(RepoPrefix, StringComparison.Ordinal) ? jobId[RepoPrefix.Length..] : null;
    public static string? JobTypeFor(string jobId) => RepoIdOf(jobId) is null ? null : JobTypes.Restic;
}

/// <summary>Resource locks (SPEC 5.): files → target folder, restic → repository, sql → instance.</summary>
public static class ResourceLocks
{
    public static IEnumerable<string> For(BackupConfig config, WorkRequest request)
    {
        if (MaintenanceIds.RepoIdOf(request.JobId) is { } repoId)
        {
            yield return "repo:" + repoId;
            yield break;
        }
        var job = config.FindJob(request.JobId);
        if (job is null) yield break;
        switch (job.Type)
        {
            case JobTypes.Files when job.Target is not null:
                yield return "target:" + job.Target.TargetId + ":" + PathUtil.Normalize(job.Target.Subdir).Trim('\\').ToLowerInvariant();
                break;
            case JobTypes.Restic when job.Restic is not null:
                yield return "repo:" + job.Restic.RepoId;
                break;
            case JobTypes.Sql when job.Sql is not null:
                yield return "sql:" + job.Sql.Instance.Trim().ToLowerInvariant();
                break;
        }
    }
}
