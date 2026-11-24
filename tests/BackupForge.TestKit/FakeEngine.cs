using System.Collections.Concurrent;
using BackupForge.Core.Config;
using BackupForge.Core.Execution;

namespace BackupForge.TestKit;

/// <summary>
/// A controllable engine: every run blocks until the test releases it (or returns immediately in auto mode).
/// </summary>
public sealed class FakeEngine : IJobEngine
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RunOutcome>> _gates = new();
    private readonly ConcurrentQueue<string> _started = new();
    private int _concurrent;
    private int _maxConcurrent;

    public bool AutoComplete { get; set; }
    public Func<JobRunContext, RunOutcome>? AutoResult { get; set; }
    public IReadOnlyCollection<string> Started => [.. _started];
    public int MaxConcurrent => _maxConcurrent;
    public int Concurrent => _concurrent;
    public ConcurrentBag<JobRunContext> Contexts { get; } = [];

    public bool CanRun(string jobType, string kind) => true;

    public async Task<RunOutcome> RunAsync(JobRunContext context, CancellationToken cancellationToken)
    {
        var key = context.Request.JobId;
        _started.Enqueue(key);
        Contexts.Add(context);
        var now = Interlocked.Increment(ref _concurrent);
        InterlockedMax(ref _maxConcurrent, now);
        try
        {
            context.Progress.Report(new RunProgress(RunPhases.Copy, 10));
            if (AutoComplete)
                return AutoResult?.Invoke(context) ?? new RunOutcome { Summary = "auto ok" };
            var gate = _gates.GetOrAdd(key, _ => new TaskCompletionSource<RunOutcome>(TaskCreationOptions.RunContinuationsAsynchronously));
            await using var reg = cancellationToken.Register(() => gate.TrySetCanceled(cancellationToken));
            var r = await gate.Task.ConfigureAwait(false);
            _gates.TryRemove(key, out _);
            return r;
        }
        finally
        {
            Interlocked.Decrement(ref _concurrent);
        }
    }

    public bool IsRunning(string jobId) => _gates.ContainsKey(jobId);

    public async Task WaitStartedAsync(string jobId, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!_gates.ContainsKey(jobId))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"{jobId} did not start");
            await Task.Delay(5).ConfigureAwait(false);
        }
    }

    public void Complete(string jobId, string result = RunResults.Ok, string summary = "done")
    {
        var gate = _gates.GetOrAdd(jobId, _ => new TaskCompletionSource<RunOutcome>(TaskCreationOptions.RunContinuationsAsynchronously));
        gate.TrySetResult(new RunOutcome { Result = result, Summary = summary });
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }
}

public static class TestConfigs
{
    /// <summary>A config with N independent files jobs (own target folders) and optional extra setup.</summary>
    public static BackupConfig Jobs(int count, Action<BackupConfig>? tweak = null)
    {
        var c = new BackupConfig
        {
            InstanceName = "SRV01",
            Targets = [new TargetConfig { Id = "t", Path = @"X:\B" }, new TargetConfig { Id = "t2", Path = @"Y:\B" }],
        };
        for (var i = 1; i <= count; i++)
        {
            c.Jobs.Add(new JobConfig
            {
                Id = "j" + i, Name = "Job " + i, Type = JobTypes.Files,
                Schedule = new ScheduleConfig { Kind = ScheduleKinds.Manual },
                Sources = [new SourceConfig { Path = @"D:\S" + i }],
                Target = new JobTargetRef { TargetId = "t", Subdir = "d" + i },
                Files = new FilesOptions(),
            });
        }
        tweak?.Invoke(c);
        return c;
    }
}
