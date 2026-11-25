using System.Globalization;
using BackupForge.Core.Abstractions;
using BackupForge.Core.Config;
using BackupForge.Core.Execution;
using BackupForge.Core.Logging;
using BackupForge.Core.Resources;
using BackupForge.Core.Scheduling;

namespace BackupForge.Engine.Restic;

/// <summary>Repository state kept in history kv for status.json (snapshot count, last snapshot, restic version).</summary>
public static class ResticState
{
    public static string LastSnapshotKey(string jobId) => "restic:last_snapshot:" + jobId;
    public static string SnapshotCountKey(string jobId) => "restic:snapshots:" + jobId;
    public const string VersionKey = "restic:version";
}

/// <summary>restic jobs (backup) and repository maintenance (forget/prune, check) — SPEC 7.</summary>
public sealed class ResticEngine : IJobEngine
{
    public static readonly TimeSpan StaleLockAge = TimeSpan.FromMinutes(30);
    private readonly ResticBinary _binary;
    private readonly IProcessRunner _processes;
    private readonly SecretMasker _masker;

    public ResticEngine(ResticBinary binary, IProcessRunner processes, SecretMasker masker)
    {
        _binary = binary;
        _processes = processes;
        _masker = masker;
    }

    public bool CanRun(string jobType, string kind) =>
        jobType == JobTypes.Restic && kind is RunKinds.Backup or RunKinds.ForgetPrune or RunKinds.Check or RunKinds.Restore;

    public static string HostName(BackupConfig cfg) => Environment.MachineName;

    private ResticRunner Runner(JobRunContext ctx) => new(_binary, _processes, ctx.Paths.ResticCache, HostName(ctx.Config));

    public async Task<RunOutcome> RunAsync(JobRunContext ctx, CancellationToken ct)
    {
        var culture = Texts.CultureFor(ctx.Config.Language);
        if (_binary.Verify() is { } bad) return RunOutcome.Failed(Texts.Format(culture, "restic.binary_bad", bad));
        if (_binary.ExpectedSha256 is null) ctx.Log.Warning($"restic binary override in use (unverified): {_binary.Path}");
        var repoId = ctx.Job?.Restic?.RepoId ?? MaintenanceIds.RepoIdOf(ctx.Request.JobId) ?? "";
        var repo = ctx.Config.FindRepo(repoId);
        if (repo is null) return RunOutcome.Failed($"unknown repository '{repoId}'");
        try
        {
            return ctx.Request.Kind switch
            {
                RunKinds.ForgetPrune => await ForgetPruneAsync(ctx, repo, culture, ct).ConfigureAwait(false),
                RunKinds.Check => await CheckAsync(ctx, repo, culture, ct).ConfigureAwait(false),
                RunKinds.Restore => await RestoreAsync(ctx, repo, culture, ct).ConfigureAwait(false),
                _ => await BackupAsync(ctx, repo, culture, ct).ConfigureAwait(false),
            };
        }
        catch (ResticConfigException ex)
        {
            return RunOutcome.Failed(Texts.Format(culture, "restic.missing_secret", ex.SecretRef));
        }
    }

    private async Task<ResticCallResult> CallWithLockRetryAsync(JobRunContext ctx, ResticRepoConfig repo, List<string> args, Action<string>? onOut, CancellationToken ct)
    {
        var runner = Runner(ctx);
        var r = await runner.RunAsync(repo, ctx.Secrets, _masker, args, onOut, ctx.Log, ct).ConfigureAwait(false);
        if (r.Killed)
        {
            await UnlockStaleAsync(ctx, repo, "cancelled run").ConfigureAwait(false);
            throw new OperationCanceledException(ct);
        }
        if (r.ExitCode == ResticExitCodes.Locked && IsOurStaleLock(r.ExitError?.Message ?? string.Join(" ", r.StdErrTail)))
        {
            ctx.Log.Warning("repository locked by a stale lock of this computer — running 'restic unlock' and retrying once");
            await UnlockStaleAsync(ctx, repo, "stale lock").ConfigureAwait(false);
            r = await runner.RunAsync(repo, ctx.Secrets, _masker, args, onOut, ctx.Log, ct).ConfigureAwait(false);
            if (r.Killed) throw new OperationCanceledException(ct);
        }
        return r;
    }

    /// <summary>SPEC 7.2: unlock only if the lock is ours (this host), older than 30 minutes (we hold the repository lock, so nothing of ours runs).</summary>
    public static bool IsOurStaleLock(string message)
    {
        var host = ResticParser.LockHost(message);
        var age = ResticParser.LockAge(message);
        return host is not null && string.Equals(host, Environment.MachineName, StringComparison.OrdinalIgnoreCase) && age is { } a && a > StaleLockAge;
    }

    private async Task UnlockStaleAsync(JobRunContext ctx, ResticRepoConfig repo, string why)
    {
        // plain "unlock" removes stale locks only (never --remove-all)
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            var r = await Runner(ctx).RunAsync(repo, ctx.Secrets, _masker, ["unlock"], null, ctx.Log, cts.Token, withHost: false).ConfigureAwait(false);
            ctx.Log.Info($"restic unlock ({why}): exit {r.ExitCode}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ctx.Log.Warning($"restic unlock failed: {ex.Message}");
        }
    }

    private RunOutcome MapFailure(ResticCallResult r, ResticRepoConfig repo, CultureInfo culture)
    {
        var msg = r.ExitError?.Message ?? string.Join(" | ", r.StdErrTail.TakeLast(5));
        msg = _masker.Apply(msg);
        return r.ExitCode switch
        {
            ResticExitCodes.RepoMissing => RunOutcome.Failed(Texts.Format(culture, "restic.no_repo", repo.Id)),
            ResticExitCodes.WrongPassword => RunOutcome.Failed(Texts.Format(culture, "restic.wrong_password", repo.PasswordRef)),
            ResticExitCodes.Locked => RunOutcome.Failed(Texts.Format(culture, "restic.locked", FirstLine(msg))),
            _ => RunOutcome.Failed(Texts.Format(culture, "restic.failed", r.ExitCode, FirstLine(msg))),
        };
    }

    private static string FirstLine(string s)
    {
        var i = s.IndexOf('\n');
        var l = i >= 0 ? s[..i] : s;
        return l.Length > 300 ? l[..299] + "…" : l;
    }

    private async Task<RunOutcome> BackupAsync(JobRunContext ctx, ResticRepoConfig repo, CultureInfo culture, CancellationToken ct)
    {
        var job = ctx.Job!;
        var opts = job.Restic!;
        var args = new List<string> { "backup" };
        foreach (var s in job.Sources)
        {
            var path = s.Path.StartsWith(ConfigValidator.StagingPrefix, StringComparison.Ordinal)
                ? StagingPaths.For(ctx.Config, ctx.Paths, s.Path[ConfigValidator.StagingPrefix.Length..])
                : s.Path;
            if (!Directory.Exists(path) && !File.Exists(path)) return RunOutcome.Failed($"source not reachable: {path}");
            args.Add(path);
        }
        args.AddRange(["--tag", "bf:" + job.Id]);
        foreach (var t in opts.Tags) args.AddRange(["--tag", t]);
        if (opts.Vss != VssModes.Never && OperatingSystem.IsWindows()) args.Add("--use-fs-snapshot");
        string? excludeFile = null;
        var excludes = job.Sources.SelectMany(s => s.Excludes).ToList();
        if (excludes.Count > 0)
        {
            excludeFile = Path.Combine(ctx.Paths.StagingRoot, $".restic-exclude-{ctx.RunId}.txt");
            Directory.CreateDirectory(ctx.Paths.StagingRoot);
            await File.WriteAllLinesAsync(excludeFile, excludes.Select(ToResticPattern), ct).ConfigureAwait(false);
            args.AddRange(["--exclude-file", excludeFile]);
        }
        if (ctx.Options.DryRun) args.Add("--dry-run");

        ResticBackupSummary? summary = null;
        var lastReport = DateTime.MinValue;
        try
        {
            ctx.Progress.Report(new RunProgress(RunPhases.Backup, 0));
            var r = await CallWithLockRetryAsync(ctx, repo, args, line =>
            {
                switch (ResticParser.MessageType(line))
                {
                    case "status" when (DateTime.UtcNow - lastReport).TotalSeconds >= 2:
                        lastReport = DateTime.UtcNow;
                        if (ResticParser.Parse<ResticStatus>(line) is { } st)
                            ctx.Progress.Report(new RunProgress(RunPhases.Backup, Math.Round(st.PercentDone * 100, 1), $"{st.FilesDone}/{st.TotalFiles}"));
                        break;
                    case "summary":
                        summary = ResticParser.Parse<ResticBackupSummary>(line);
                        break;
                }
            }, ct).ConfigureAwait(false);

            if (r.ExitCode is not (ResticExitCodes.Ok or ResticExitCodes.PartialRead)) return MapFailure(r, repo, culture);
            var outcome = new RunOutcome
            {
                FilesProcessed = summary?.TotalFilesProcessed ?? 0,
                BytesProcessed = summary?.TotalBytesProcessed ?? 0,
                BytesTransferred = summary?.DataAddedPacked ?? 0,
                FilesCopied = (summary?.FilesNew ?? 0) + (summary?.FilesChanged ?? 0),
            };
            foreach (var e in r.Errors) outcome.AddWarning(e.Item, _masker.Apply(e.Message));
            var shortId = summary?.SnapshotId is { Length: >= 8 } sid ? sid[..8] : "-";
            outcome.Summary = Texts.Format(culture, "restic.summary", shortId, summary?.FilesNew ?? 0, summary?.FilesChanged ?? 0, SizeText.Format(summary?.DataAddedPacked ?? 0, culture));
            if (r.ExitCode == ResticExitCodes.PartialRead)
            {
                outcome.Result = RunResults.Warning;
                outcome.Summary += " · " + Texts.Format(culture, "restic.partial", Math.Max(1, r.Errors.Count));
            }
            if (!ctx.Options.DryRun && summary?.SnapshotId is { } id)
            {
                ctx.State.SetValue(ResticState.LastSnapshotKey(job.Id), id[..Math.Min(8, id.Length)]);
                await UpdateSnapshotCountAsync(ctx, repo, job.Id, ct).ConfigureAwait(false);
            }
            return outcome;
        }
        finally
        {
            if (excludeFile is not null) try { File.Delete(excludeFile); } catch (IOException) { }
        }
    }

    private async Task UpdateSnapshotCountAsync(JobRunContext ctx, ResticRepoConfig repo, string jobId, CancellationToken ct)
    {
        try
        {
            var r = await Runner(ctx).RunAsync(repo, ctx.Secrets, _masker, ["snapshots", "--tag", "bf:" + jobId], null, null, ct).ConfigureAwait(false);
            if (r.ExitCode == 0)
                ctx.State.SetValue(ResticState.SnapshotCountKey(jobId), ResticParser.Snapshots(r.StdOutText).Count.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
        {
            ctx.Log.Warning("snapshot count not available: " + ex.Message);
        }
    }

    private async Task<RunOutcome> ForgetPruneAsync(JobRunContext ctx, ResticRepoConfig repo, CultureInfo culture, CancellationToken ct)
    {
        if (repo.AppendOnlyServer) return new RunOutcome { Result = RunResults.Skipped, Summary = Texts.Get("restic.append_only", culture) };
        ctx.Progress.Report(new RunProgress(RunPhases.ForgetPrune, null));
        var r0 = repo.Retention;
        var args = new List<string> { "forget", "--prune", "--group-by", "host,tags" };
        void Keep(string flag, int? v) { if (v is > 0) args.AddRange([flag, v.Value.ToString(CultureInfo.InvariantCulture)]); }
        Keep("--keep-last", r0.KeepLast);
        Keep("--keep-daily", r0.KeepDaily);
        Keep("--keep-weekly", r0.KeepWeekly);
        Keep("--keep-monthly", r0.KeepMonthly);
        Keep("--keep-yearly", r0.KeepYearly);
        if (args.Count == 4) return RunOutcome.Failed("retention has no keep rule — refusing to forget everything");
        if (ctx.Options.DryRun) args.Add("--dry-run");
        var r = await CallWithLockRetryAsync(ctx, repo, args, null, ct).ConfigureAwait(false);
        if (r.ExitCode is not (ResticExitCodes.Ok or ResticExitCodes.PartialRead)) return MapFailure(r, repo, culture);
        var groups = ResticParser.ForgetGroups(r.StdOutText);
        var removed = groups.Sum(g => g.Remove?.Count ?? 0);
        var kept = groups.Sum(g => g.Keep?.Count ?? 0);
        foreach (var g in groups)
        {
            var tag = g.Tags?.FirstOrDefault(t => t.StartsWith("bf:", StringComparison.Ordinal));
            if (tag is not null) ctx.State.SetValue(ResticState.SnapshotCountKey(tag[3..]), (g.Keep?.Count ?? 0).ToString(CultureInfo.InvariantCulture));
        }
        return new RunOutcome
        {
            Result = r.ExitCode == ResticExitCodes.PartialRead ? RunResults.Warning : RunResults.Ok,
            Summary = Texts.Format(culture, "restic.forget", removed, kept),
        };
    }

    private async Task<RunOutcome> CheckAsync(JobRunContext ctx, ResticRepoConfig repo, CultureInfo culture, CancellationToken ct)
    {
        ctx.Progress.Report(new RunProgress(RunPhases.Check, null));
        var subset = repo.Maintenance.CheckReadDataSubset;
        var args = new List<string> { "check" };
        if (TimeSpec.TryParsePercent(subset, out _)) args.AddRange(["--read-data-subset", subset]);
        var r = await CallWithLockRetryAsync(ctx, repo, args, null, ct).ConfigureAwait(false);
        var summaryLine = r.StdOut.LastOrDefault(l => ResticParser.MessageType(l) == "summary");
        var summary = summaryLine is null ? null : ResticParser.Parse<ResticCheckSummary>(summaryLine);
        if (r.ExitCode == 0) return new RunOutcome { Summary = Texts.Format(culture, "restic.check_ok", subset) };
        if (r.ExitCode == ResticExitCodes.Failed && summary is not null)
        {
            var o = RunOutcome.Failed(Texts.Format(culture, "restic.check_failed", summary.NumErrors));
            foreach (var e in r.Errors) o.AddError(null, _masker.Apply(e.Message));
            return o;
        }
        return MapFailure(r, repo, culture);
    }

    /// <summary>Restore into a new folder (SPEC 13.): parameters snapshot, path (optional, inside the snapshot), target.</summary>
    private async Task<RunOutcome> RestoreAsync(JobRunContext ctx, ResticRepoConfig repo, CultureInfo culture, CancellationToken ct)
    {
        var p = ctx.Request.Parameters;
        var snapshot = p.GetValueOrDefault("snapshot") ?? "latest";
        var target = p.GetValueOrDefault("target") ?? throw new InvalidOperationException("restore target missing");
        var include = p.GetValueOrDefault("path");
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            return RunOutcome.Failed($"restore target is not empty: {target}");
        Directory.CreateDirectory(target);
        var client = new ResticClient(_binary, _processes, ctx.Paths.ResticCache, _masker);
        var args = new List<string>();
        var summary = await client.RestoreAsync(repo, ctx.Secrets, snapshot, string.IsNullOrEmpty(include) ? null : include, target, overwrite: false, ctx.Log, ct).ConfigureAwait(false);
        return new RunOutcome
        {
            FilesCopied = summary?.FilesRestored ?? 0,
            BytesTransferred = summary?.BytesRestored ?? 0,
            Summary = Texts.Format(culture, "restic.restore", summary?.FilesRestored ?? 0, SizeText.Format(summary?.BytesRestored ?? 0, culture), target),
        };
    }

    /// <summary>Our glob syntax → restic exclude patterns (restic: '*', '**', path from root with a leading separator).</summary>
    public static string ToResticPattern(string pattern)
    {
        var p = pattern.Trim();
        if (p.StartsWith('\\') && p.EndsWith('\\') && p.Trim('\\').Length > 0 && !p.Trim('\\').Contains('\\')) return p.Trim('\\'); // folder name anywhere
        return p.TrimEnd('\\');
    }
}
