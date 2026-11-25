using BackupForge.Core.Execution;

namespace BackupForge.Service;

/// <summary>
/// Development / integration-test engine (only active with BACKUPFORGE_TEST_ENGINE=1): waits
/// BACKUPFORGE_TEST_ENGINE_MS milliseconds (default 300) and succeeds. Never registered otherwise.
/// </summary>
public sealed class TestEngine : IJobEngine
{
    public const string EnvVar = "BACKUPFORGE_TEST_ENGINE";
    public const string DelayEnvVar = "BACKUPFORGE_TEST_ENGINE_MS";

    public static bool Enabled => Environment.GetEnvironmentVariable(EnvVar) == "1";

    public bool CanRun(string jobType, string kind) => true;

    public async Task<RunOutcome> RunAsync(JobRunContext context, CancellationToken cancellationToken)
    {
        var ms = int.TryParse(Environment.GetEnvironmentVariable(DelayEnvVar), out var v) ? v : 300;
        context.Log.Info($"test engine: {context.Request.JobId} ({context.Request.Kind}), {ms} ms");
        context.Progress.Report(new RunProgress(RunPhases.Copy, 50));
        await Task.Delay(ms, cancellationToken).ConfigureAwait(false);
        return new RunOutcome { Summary = $"test engine ok ({context.Request.Kind})", FilesCopied = 1, BytesTransferred = 1 };
    }
}
