using BackupForge.Core.Abstractions;
using BackupForge.Core.Config;
using BackupForge.Core.Execution;
using BackupForge.Core.Logging;

namespace BackupForge.Engine.Restic;

public sealed record ResticCallResult(int ExitCode, List<string> StdOut, List<ResticError> Errors, ResticExitError? ExitError, IReadOnlyList<string> StdErrTail, bool Killed)
{
    public string StdOutText => string.Join("\n", StdOut);
}

/// <summary>
/// Starts restic for one repository. Secrets go only into the environment (RESTIC_PASSWORD, RESTIC_REPOSITORY, env_refs),
/// never into the command line (SPEC 7.2).
/// </summary>
public sealed class ResticRunner(ResticBinary binary, IProcessRunner processes, string cacheDir, string host)
{
    public ResticBinary Binary => binary;
    public string Host => host;

    /// <summary>Environment for a repository; throws if a secret is missing.</summary>
    public static Dictionary<string, string> Environment(ResticRepoConfig repo, ISecretStore secrets, SecretMasker? masker)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["RESTIC_REPOSITORY"] = repo.Location,
            ["RESTIC_PASSWORD"] = secrets.Get(repo.PasswordRef) ?? throw new ResticConfigException(repo.PasswordRef),
            ["RESTIC_PROGRESS_FPS"] = "0.5",
        };
        foreach (var (name, reference) in repo.EnvRefs)
            env[name] = secrets.Get(reference) ?? throw new ResticConfigException(reference);
        masker?.RegisterAll(env.Where(kv => kv.Key != "RESTIC_PROGRESS_FPS" && kv.Key != "RESTIC_REPOSITORY").Select(kv => kv.Value));
        return env;
    }

    public async Task<ResticCallResult> RunAsync(ResticRepoConfig repo, ISecretStore secrets, SecretMasker? masker, IEnumerable<string> args,
        Action<string>? onStdOut, IRunLog? log, CancellationToken ct, bool withHost = true)
    {
        var argList = new List<string>(args) { "--json", "--cache-dir", cacheDir };
        if (withHost) { argList.Add("--host"); argList.Add(host); }
        var spec = new ProcessSpec { FileName = binary.Path, Arguments = argList, Environment = Environment(repo, secrets, masker) };
        log?.Info("restic " + string.Join(' ', argList.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));
        var stdout = new List<string>();
        var errors = new List<ResticError>();
        ResticExitError? exit = null;
        var result = await processes.RunAsync(spec,
            line =>
            {
                if (onStdOut is not null) onStdOut(line);
                else lock (stdout) stdout.Add(line);
            },
            line =>
            {
                if (ResticParser.ParseExitError(line) is { } ee) { exit = ee; log?.Error("restic: " + ee.Message); return; }
                if (ResticParser.ParseError(line) is { } e)
                {
                    lock (errors) errors.Add(e);
                    log?.Warning("restic: " + (e.Item is null ? e.Message : $"{e.Item}: {e.Message}"));
                    return;
                }
                log?.Info("restic: " + line);
            }, ct).ConfigureAwait(false);
        return new ResticCallResult(result.ExitCode, stdout, errors, exit, result.StdErrTail, result.Killed);
    }
}

public sealed class ResticConfigException(string secretRef) : Exception($"secret '{secretRef}' is not set")
{
    public string SecretRef { get; } = secretRef;
}

public static class ResticExitCodes
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int GoRuntime = 2;
    public const int PartialRead = 3;
    public const int RepoMissing = 10;
    public const int Locked = 11;
    public const int WrongPassword = 12;
    public const int Cancelled = 130;
}
