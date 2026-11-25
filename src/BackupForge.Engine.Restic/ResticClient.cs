using BackupForge.Core.Abstractions;
using BackupForge.Core.Config;
using BackupForge.Core.Logging;

namespace BackupForge.Engine.Restic;

/// <summary>Interactive repository operations for the GUI / CLI: init (only on explicit request), snapshots, ls, restore, version.</summary>
public sealed class ResticClient(ResticBinary binary, IProcessRunner processes, string cacheDir, SecretMasker masker)
{
    private ResticRunner Runner() => new(binary, processes, cacheDir, Environment.MachineName);

    private static void Throw(ResticCallResult r, string what)
    {
        var msg = r.ExitError?.Message ?? string.Join(" | ", r.StdErrTail.TakeLast(3));
        throw new ResticOperationException($"{what} failed (exit {r.ExitCode}): {msg}", r.ExitCode);
    }

    /// <summary>SPEC 7.2: never automatic; the caller must have asked the user.</summary>
    public async Task<string> InitAsync(ResticRepoConfig repo, ISecretStore secrets, CancellationToken ct)
    {
        var r = await Runner().RunAsync(repo, secrets, masker, ["init"], null, null, ct, withHost: false).ConfigureAwait(false);
        if (r.ExitCode != 0) Throw(r, "init");
        return masker.Apply(r.StdOutText);
    }

    public async Task<List<ResticSnapshot>> SnapshotsAsync(ResticRepoConfig repo, ISecretStore secrets, string? tag, CancellationToken ct)
    {
        var args = new List<string> { "snapshots" };
        if (tag is not null) args.AddRange(["--tag", tag]);
        var r = await Runner().RunAsync(repo, secrets, masker, args, null, null, ct, withHost: false).ConfigureAwait(false);
        if (r.ExitCode != 0) Throw(r, "snapshots");
        return ResticParser.Snapshots(r.StdOutText);
    }

    public async Task<List<ResticNode>> LsAsync(ResticRepoConfig repo, ISecretStore secrets, string snapshot, string? path, CancellationToken ct)
    {
        var args = new List<string> { "ls", snapshot };
        if (!string.IsNullOrEmpty(path)) args.Add(path);
        var r = await Runner().RunAsync(repo, secrets, masker, args, null, null, ct, withHost: false).ConfigureAwait(false);
        if (r.ExitCode != 0) Throw(r, "ls");
        return [.. r.StdOut.Where(l => ResticParser.MessageType(l) == "node").Select(ResticParser.Parse<ResticNode>).Where(n => n is not null).Select(n => n!)];
    }

    public async Task<ResticRestoreSummary?> RestoreAsync(ResticRepoConfig repo, ISecretStore secrets, string snapshot, string? include, string target, bool overwrite,
        Core.Execution.IRunLog? log, CancellationToken ct)
    {
        var args = new List<string> { "restore", snapshot, "--target", target };
        if (!string.IsNullOrEmpty(include)) args.AddRange(["--include", include]);
        if (!overwrite) args.AddRange(["--overwrite", "never"]);
        ResticRestoreSummary? summary = null;
        var r = await Runner().RunAsync(repo, secrets, masker, args, line =>
        {
            if (ResticParser.MessageType(line) == "summary") summary = ResticParser.Parse<ResticRestoreSummary>(line);
        }, log, ct, withHost: false).ConfigureAwait(false);
        if (r.Killed) throw new OperationCanceledException(ct);
        if (r.ExitCode != 0) Throw(r, "restore");
        return summary;
    }

    public async Task<string?> VersionAsync(CancellationToken ct)
    {
        var spec = new ProcessSpec { FileName = binary.Path, Arguments = ["version", "--json"] };
        var lines = new List<string>();
        var r = await processes.RunAsync(spec, l => lines.Add(l), null, ct).ConfigureAwait(false);
        if (r.ExitCode != 0) return null;
        foreach (var l in lines)
        {
            try
            {
                using var d = System.Text.Json.JsonDocument.Parse(l);
                if (d.RootElement.TryGetProperty("version", out var v)) return v.GetString();
            }
            catch (System.Text.Json.JsonException) { }
        }
        return null;
    }
}

public sealed class ResticOperationException(string message, int exitCode) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}
