using System.Collections.Concurrent;
using System.Globalization;
using BackupForge.Core.Abstractions;
using BackupForge.Core.Config;
using BackupForge.Core.Execution;
using BackupForge.Core.Resources;
using BackupForge.Core.Status;

namespace BackupForge.Engine.Files;

public sealed class FileEngineOptions
{
    /// <summary>Wait before the 1st, 2nd, 3rd retry of a file (SPEC 6.3: 1, 5, 15 s).</summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; init; } = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)];
    /// <summary>Null = from the file system (Windows: case-insensitive).</summary>
    public bool? CaseInsensitive { get; init; }
}

/// <summary>The files job (SPEC 6.): plan → delete guard → execute → Versions retention.</summary>
public sealed class FileJobEngine : IJobEngine
{
    private readonly IFileSystem _fs;
    private readonly ITargetConnector _connector;
    private readonly ISnapshotProvider _vss;
    private readonly FileEngineOptions _options;

    public FileJobEngine(IFileSystem fs, ITargetConnector connector, ISnapshotProvider vss, FileEngineOptions? options = null)
    {
        _fs = fs;
        _connector = connector;
        _vss = vss;
        _options = options ?? new FileEngineOptions();
    }

    public bool CanRun(string jobType, string kind) => jobType == JobTypes.Files && kind == RunKinds.Backup;

    public Task<RunOutcome> RunAsync(JobRunContext ctx, CancellationToken ct) => Task.Run(() => Run(ctx, ct), ct);

    private sealed class TargetUnavailableException(string message, Exception inner) : Exception(message, inner);

    private RunOutcome Run(JobRunContext ctx, CancellationToken ct)
    {
        var job = ctx.Job ?? throw new InvalidOperationException("files engine needs a job");
        var files = job.Files ?? new FilesOptions();
        var culture = Texts.CultureFor(ctx.Config.Language);
        var target = ctx.Config.FindTarget(job.Target!.TargetId)!;
        var mirrorRoot = JobPaths.MirrorPath(target, job.Target);
        var versionsRoot = files.Versioning.Enabled ? JobPaths.VersionsPath(target, job.Target, files.Versioning) : null;
        var log = ctx.Log;
        var outcome = new RunOutcome();
        var dry = ctx.Options.DryRun;
        var startLocal = ctx.Clock.LocalNow();

        // 1. target
        IDisposable connection;
        try
        {
            connection = _connector.Connect(target, ctx.Secrets);
        }
        catch (TargetConnectException ex)
        {
            return RunOutcome.Failed(Texts.Format(culture, "files.target_unreachable", ex.Message));
        }
        using var _ = connection;
        if (!TargetRootReachable(target.Path))
            return RunOutcome.Failed(Texts.Format(culture, "files.target_unreachable", target.Path));

        // 2. sources (never mirror an empty or unreachable source)
        var roots = new List<SourceRoot>();
        foreach (var src in job.Sources)
        {
            var path = ResolveSource(ctx, src.Path);
            if (!_fs.DirectoryExists(path)) return RunOutcome.Failed(Texts.Format(culture, "files.source_missing", path));
            bool any;
            try { any = _fs.Enumerate(path).Any(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return RunOutcome.Failed(Texts.Format(culture, "files.source_missing", $"{path} ({ex.Message})")); }
            if (!any) return RunOutcome.Failed(Texts.Format(culture, "files.source_empty", path));
            roots.Add(new SourceRoot(path, path, job.Sources.Count == 1 ? "" : LeafName(path)));
        }

        // 3. VSS
        ISnapshotSession? snapshot = null;
        try
        {
            if (files.Vss != VssModes.Never && !dry && roots.Any(r => !r.OriginalPath.StartsWith(@"\\", StringComparison.Ordinal)))
            {
                ctx.Progress.Report(new RunProgress(RunPhases.Vss, null));
                try
                {
                    if (!_vss.IsSupported) throw new NotSupportedException("VSS is not available");
                    snapshot = _vss.Create(roots.Where(r => !r.OriginalPath.StartsWith(@"\\", StringComparison.Ordinal)).Select(r => r.OriginalPath).ToList(), ct);
                    foreach (var s in snapshot.Snapshots)
                    {
                        ctx.VssSnapshotCreated?.Invoke(s.SnapshotId, s.SetId);
                        log.Info($"VSS snapshot {s.SnapshotId} for {s.Volume}");
                    }
                    roots = [.. roots.Select(r => r.OriginalPath.StartsWith(@"\\", StringComparison.Ordinal) ? r : r with { ReadPath = snapshot.Translate(r.OriginalPath) })];
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    if (files.Vss == VssModes.Required) return RunOutcome.Failed(Texts.Format(culture, "files.vss_required", ex.Message));
                    var msg = Texts.Format(culture, "files.vss_failed", ex.Message);
                    log.Warning(msg);
                    outcome.AddWarning(null, msg);
                }
            }

            // 4. plan
            ctx.Progress.Report(new RunProgress(RunPhases.Plan, null));
            if (!dry) CleanupParts(mirrorRoot, log, ctx.ProtectedPaths.Where(pp => pp.JobId != job.Id).ToList());
            var caseInsensitive = _options.CaseInsensitive ?? !_fs.IsCaseSensitive;
            var excludes = ExcludeMatcher.For(job.Sources.SelectMany(s => s.Excludes), files.UseDefaultExcludes);
            var planner = new Planner(_fs, excludes, files.Mode == FilesModes.Mirror, files.Compare == CompareModes.Content,
                files.CloudFiles == CloudFilesModes.Skip, caseInsensitive, ct);
            FilePlan plan;
            try { plan = planner.Build(roots, mirrorRoot); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return RunOutcome.Failed(Texts.Format(culture, "files.target_unreachable", ex.Message));
            }
            foreach (var i in plan.Infos) log.Info(i);
            foreach (var (path, msg) in plan.Warnings) { log.Warning($"{path}: {msg}"); outcome.AddWarning(path, msg); }
            if (plan.CloudOnly.Count > 0 && files.CloudFiles == CloudFilesModes.Fail)
                return RunOutcome.Failed(Texts.Format(culture, "files.cloud_failed", plan.CloudOnly[0]));

            // 4b. safety net (CR-001 1b.): never delete inside a folder of another job / BackupForge, whatever the config says
            var foreign = ctx.ProtectedPaths.Where(pp => pp.JobId != job.Id).ToList();
            if (foreign.Count > 0)
            {
                var blocked = plan.Items
                    .Where(i => i.Action is PlanAction.DeleteFile or PlanAction.DeleteDir)
                    .Select(i => (Item: i, Owner: ProtectedPaths.Owner(foreign, job.Id, Path.Combine(mirrorRoot, i.Rel.Replace('\\', Path.DirectorySeparatorChar)))))
                    .Where(x => x.Owner is not null)
                    .ToList();
                if (blocked.Count > 0)
                {
                    foreach (var (item, _) in blocked) plan.Items.Remove(item);
                    var owners = string.Join(", ", blocked.Select(b => b.Owner!.JobId ?? "BackupForge").Distinct());
                    var msg = Texts.Format(culture, "files.protected_skipped", blocked.Count(b => b.Item.Action == PlanAction.DeleteFile), owners);
                    log.Warning(msg);
                    outcome.AddWarning(null, msg);
                }
            }

            // 5. delete guard (SPEC 6.5) — before anything is deleted or moved to Versions
            var deletes = plan.DeleteFileCount;
            var guardPct = plan.TargetFileCount == 0 ? 0 : 100.0 * deletes / plan.TargetFileCount;
            var guardTrips = files.Mode == FilesModes.Mirror && deletes >= files.DeleteGuardMinFiles && guardPct >= files.DeleteGuardPct && deletes > 0;
            var approvedKey = DeleteGuardState.ApprovedKey(job.Id);
            var trippedKey = DeleteGuardState.TrippedKey(job.Id);
            var guardBlocked = false;
            if (guardTrips)
            {
                var approved = ctx.Options.AllowMassDelete || ctx.State.GetValue(approvedKey) is not null;
                if (approved)
                {
                    log.Warning($"delete guard: {deletes} files ({guardPct:0.#}%) — allowed (one-time approval)");
                    if (!dry) ctx.State.SetValue(approvedKey, null);
                }
                else
                {
                    guardBlocked = true;
                    plan.RemoveDeletes();
                    log.Error($"delete guard tripped: {deletes} of {plan.TargetFileCount} files ({guardPct:0.#}%) would be deleted");
                }
            }

            if (dry)
            {
                foreach (var item in plan.Items.Take(20000)) log.Info($"plan: {item.Action} {item.Rel}{(item.TargetName is null ? "" : " → " + item.TargetName)}");
                var vcount = versionsRoot is null ? 0 : plan.UpdateCount + (guardBlocked ? 0 : plan.Items.Count(i => i.Action == PlanAction.DeleteFile));
                outcome.Summary = Texts.Format(culture, "files.dryrun", plan.CopyCount + plan.UpdateCount, vcount, guardBlocked ? 0 : deletes, SizeText.Format(plan.CopyBytes, culture));
                outcome.FilesProcessed = plan.SourceFileCount;
                outcome.BytesProcessed = plan.SourceBytes;
                if (guardBlocked)
                {
                    outcome.Result = RunResults.Failed;
                    outcome.DeleteGuardTripped = true;
                    outcome.Summary = Texts.Format(culture, "files.guard", deletes, guardPct) + " · " + outcome.Summary;
                }
                else outcome.Result = outcome.WarningCount > 0 ? RunResults.Warning : RunResults.Ok;
                return outcome;
            }

            // 6. execute
            var stats = Execute(ctx, plan, mirrorRoot, versionsRoot, VersionsRetention.Stamp(startLocal), files, outcome, ct);

            outcome.FilesProcessed = plan.SourceFileCount;
            outcome.BytesProcessed = plan.SourceBytes;
            outcome.FilesCopied = stats.Copied;
            outcome.FilesVersioned = stats.Versioned;
            outcome.FilesDeleted = stats.Deleted;
            outcome.FilesSkipped = stats.Failed + plan.CloudOnly.Count;
            outcome.BytesTransferred = stats.Bytes;

            // result
            var failedRatio = plan.SourceFileCount == 0 ? 0 : (double)stats.Failed / plan.SourceFileCount;
            if (guardBlocked)
            {
                outcome.Result = RunResults.Failed;
                outcome.DeleteGuardTripped = true;
                ctx.State.SetValue(trippedKey, ctx.Clock.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            }
            else
            {
                ctx.State.SetValue(trippedKey, null);
                if (stats.Failed > 0 && (failedRatio >= 0.01 || stats.Failed >= 100)) outcome.Result = RunResults.Failed;
                else if (stats.Failed > 0 || outcome.WarningCount > 0) outcome.Result = RunResults.Warning;
            }

            // 7. retention (not after a failed run, SPEC 6.4)
            if (versionsRoot is not null && outcome.Result != RunResults.Failed)
            {
                ctx.Progress.Report(new RunProgress(RunPhases.Retention, null));
                var ret = VersionsRetention.Apply(_fs, versionsRoot, files.Versioning.KeepDays, files.Versioning.MaxVersionsPerFile, startLocal.DateTime, ct);
                if (ret.FoldersDeleted + ret.FilesDeleted > 0) log.Info($"versions retention: {ret.FoldersDeleted} folder(s), {ret.FilesDeleted} file version(s) deleted");
                foreach (var e in ret.Errors) { log.Warning("versions retention: " + e); outcome.AddWarning(null, "versions retention: " + e); }
            }

            var summary = Texts.Format(culture, "files.summary", stats.Copied, stats.Versioned, stats.Deleted, SizeText.Format(stats.Bytes, culture));
            if (outcome.FilesSkipped > 0) summary += Texts.Format(culture, "files.summary_skipped", outcome.FilesSkipped);
            if (guardBlocked) summary = Texts.Format(culture, "files.guard", deletes, guardPct) + " · " + summary;
            else if (outcome.Result == RunResults.Failed && stats.Failed > 0) summary = Texts.Format(culture, "files.too_many_errors", stats.Failed, plan.SourceFileCount) + " · " + summary;
            outcome.Summary = summary;
            return outcome;
        }
        catch (AggregateException ae) when (ae.Flatten().InnerExceptions.OfType<OperationCanceledException>().FirstOrDefault() is { } oce)
        {
            throw oce;
        }
        catch (AggregateException ae) when (ae.Flatten().InnerExceptions.OfType<TargetUnavailableException>().FirstOrDefault() is { } tu)
        {
            log.Error(tu.ToString());
            outcome.Result = RunResults.Failed;
            outcome.Summary = Texts.Format(culture, "files.target_unreachable", tu.InnerException?.Message ?? tu.Message);
            return outcome;
        }
        catch (TargetUnavailableException ex)
        {
            log.Error(ex.ToString());
            outcome.Result = RunResults.Failed;
            outcome.Summary = Texts.Format(culture, "files.target_unreachable", ex.InnerException?.Message ?? ex.Message);
            return outcome;
        }
        finally
        {
            if (snapshot is not null)
            {
                var ids = snapshot.Snapshots.Select(s => s.SnapshotId).ToList();
                try
                {
                    snapshot.Dispose();
                    foreach (var id in ids) ctx.VssSnapshotDeleted?.Invoke(id);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    log.Warning("VSS snapshot cleanup failed: " + ex.Message);
                }
            }
        }
    }

    private sealed class Stats
    {
        public long Copied;
        public long Versioned;
        public long Deleted;
        public long Failed;
        public long Bytes;
    }

    private Stats Execute(JobRunContext ctx, FilePlan plan, string mirrorRoot, string? versionsRoot, string stamp, FilesOptions files, RunOutcome outcome, CancellationToken ct)
    {
        var log = ctx.Log;
        var stats = new Stats();
        var itemsLock = new Lock();
        string Abs(string rel) => rel.Length == 0 ? mirrorRoot : Path.Combine(mirrorRoot, rel.Replace('\\', Path.DirectorySeparatorChar));
        string VersionPath(string rel) => Path.Combine(versionsRoot!, stamp, rel.Replace('\\', Path.DirectorySeparatorChar));

        void MoveToVersions(string absPath, string rel)
        {
            var dest = VersionPath(rel);
            _fs.CreateDirectory(Path.GetDirectoryName(dest)!);
            _fs.MoveFile(absPath, dest, overwrite: true);
            Interlocked.Increment(ref stats.Versioned);
        }

        void Fail(string rel, Exception ex)
        {
            CheckTarget(mirrorRoot, ex);
            Interlocked.Increment(ref stats.Failed);
            var msg = ex.Message;
            log.Error($"{rel}: {msg}");
            lock (itemsLock) outcome.AddError(rel, msg);
        }

        _fs.CreateDirectory(mirrorRoot);

        // case renames (top-down), new folders
        foreach (var item in plan.Items.Where(i => i.Action == PlanAction.RenameCase).OrderBy(i => i.Rel.Length))
        {
            try
            {
                var from = Abs(item.Rel);
                var to = Path.Combine(Path.GetDirectoryName(from)!, item.TargetName!);
                var tmp = from + ".bfcase";
                var isDir = _fs.DirectoryExists(from);
                if (isDir) { _fs.MoveDirectory(from, tmp); _fs.MoveDirectory(tmp, to); }
                else { _fs.MoveFile(from, tmp, false); _fs.MoveFile(tmp, to, false); }
                log.Info($"renamed (case): {item.Rel} → {item.TargetName}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Fail(item.Rel, ex); }
        }

        // deletions first (frees space); files are versioned one by one, folders removed when empty
        ctx.Progress.Report(new RunProgress(RunPhases.Copy, 0));
        foreach (var item in plan.Items.Where(i => i.Action == PlanAction.DeleteFile))
        {
            ct.ThrowIfCancellationRequested();
            var abs = Abs(item.Rel);
            try
            {
                if (!_fs.FileExists(abs)) continue;
                if (versionsRoot is not null) MoveToVersions(abs, item.Rel);
                else _fs.DeleteFile(abs);
                stats.Deleted++;
                log.Info($"{(versionsRoot is null ? "deleted" : "versioned (deleted)")}: {item.Rel}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Fail(item.Rel, ex); }
        }
        foreach (var item in plan.Items.Where(i => i.Action == PlanAction.DeleteDir).OrderByDescending(i => i.Rel.Length))
        {
            var abs = Abs(item.Rel);
            try
            {
                if (_fs.DirectoryExists(abs) && !_fs.Enumerate(abs).Any()) _fs.DeleteDirectory(abs, recursive: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.Warning($"{item.Rel}: folder not removed: {ex.Message}"); }
        }
        foreach (var item in plan.Items.Where(i => i.Action == PlanAction.CreateDir).OrderBy(i => i.Rel.Length))
        {
            try { _fs.CreateDirectory(Abs(item.Rel)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Fail(item.Rel, ex); }
        }
        foreach (var item in plan.Items.Where(i => i.Action == PlanAction.TouchTime))
        {
            try { new FileCopier(_fs, null).ApplyMetadata(item.Source!, Abs(item.Rel)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.Warning($"{item.Rel}: {ex.Message}"); }
        }

        // copies (parallel)
        var copies = plan.Copies.ToList();
        var total = Math.Max(1, plan.CopyBytes);
        long done = 0;
        var lastReport = DateTime.UtcNow;
        var throttle = files.ThrottleMbps is { } mbps ? new Throttle(mbps) : null;
        var copier = new FileCopier(_fs, throttle);
        var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(files.Parallelism, 1, 16), CancellationToken = ct };
        Parallel.ForEach(copies, po, item =>
        {
            var dest = Abs(item.Rel);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var bytes = copier.Copy(item.Source!, dest,
                        item.Action == PlanAction.Update && versionsRoot is not null ? p => MoveToVersions(p, item.Rel) : null,
                        n =>
                        {
                            var d = Interlocked.Add(ref done, n);
                            if ((DateTime.UtcNow - lastReport).TotalSeconds >= 2)
                            {
                                lastReport = DateTime.UtcNow;
                                ctx.Progress.Report(new RunProgress(RunPhases.Copy, Math.Min(100, 100.0 * d / total)));
                            }
                        }, ct);
                    Interlocked.Add(ref stats.Bytes, bytes);
                    Interlocked.Increment(ref stats.Copied);
                    return;
                }
                catch (OperationCanceledException) { throw; }
                catch (FileNotFoundException ex)
                {
                    // vanished from the source during the run: not an error of the backup
                    log.Warning($"{item.Rel}: source disappeared during the run ({ex.Message})");
                    lock (itemsLock) outcome.AddWarning(item.Rel, "source disappeared during the run");
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    CheckTarget(mirrorRoot, ex);
                    if (attempt >= _options.RetryDelays.Count) { Fail(item.Rel, ex); return; }
                    log.Warning($"{item.Rel}: {ex.Message} — retry {attempt + 1} in {_options.RetryDelays[attempt].TotalSeconds:0} s");
                    if (ct.WaitHandle.WaitOne(_options.RetryDelays[attempt])) ct.ThrowIfCancellationRequested();
                }
            }
        });
        ctx.Progress.Report(new RunProgress(RunPhases.Copy, 100));
        return stats;
    }

    /// <summary>A lost target aborts the whole run (SPEC 6.3: network failure → failed immediately).</summary>
    private void CheckTarget(string mirrorRoot, Exception cause)
    {
        bool ok;
        try { ok = _fs.DirectoryExists(mirrorRoot); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ok = false; }
        if (!ok) throw new TargetUnavailableException(mirrorRoot, cause);
    }

    private bool TargetRootReachable(string targetPath)
    {
        try
        {
            if (_fs.DirectoryExists(targetPath)) return true;
            // a local target folder may be created, but only if its parent (the drive / share) exists
            var parent = Path.GetDirectoryName(targetPath.TrimEnd('\\', '/'));
            if (!string.IsNullOrEmpty(parent) && _fs.DirectoryExists(parent))
            {
                _fs.CreateDirectory(targetPath);
                return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Removes leftover *.bfpart files of an interrupted run — only inside the job's own mirror folder.</summary>
    private void CleanupParts(string mirrorRoot, IRunLog log, IReadOnlyList<ProtectedPath> foreign)
    {
        if (!_fs.DirectoryExists(mirrorRoot)) return;
        var stack = new Stack<string>();
        stack.Push(mirrorRoot);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<FsEntry> entries;
            try { entries = _fs.Enumerate(dir).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var e in entries)
            {
                if (e.IsReparsePoint) continue;
                if (ProtectedPaths.Owner(foreign, "", e.FullPath) is not null) continue; // another job's folder (CR-001 1b.)
                if (e.IsDirectory) stack.Push(e.FullPath);
                else if (e.Name.EndsWith(FileCopier.PartSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    try { _fs.DeleteFile(e.FullPath); log.Info($"removed leftover {e.FullPath}"); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.Warning($"{e.FullPath}: {ex.Message}"); }
                }
            }
        }
    }

    private static string ResolveSource(JobRunContext ctx, string path)
    {
        if (!path.StartsWith(ConfigValidator.StagingPrefix, StringComparison.Ordinal)) return path;
        var jobId = path[ConfigValidator.StagingPrefix.Length..];
        return StagingPaths.For(ctx.Config, ctx.Paths, jobId);
    }

    private static string LeafName(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var i = trimmed.LastIndexOfAny(['\\', '/']);
        var leaf = i >= 0 ? trimmed[(i + 1)..] : trimmed;
        return leaf.Replace(":", "", StringComparison.Ordinal);
    }

    /// <summary>Folder count / oldest stamp of the Versions folder (for status.json).</summary>
    public VersionsStatus? VersionsInfo(BackupConfig config, JobConfig job)
    {
        if (job.Files is not { Versioning.Enabled: true } || job.Target is null) return null;
        var target = config.FindTarget(job.Target.TargetId);
        if (target is null) return null;
        var path = JobPaths.VersionsPath(target, job.Target, job.Files.Versioning);
        if (path is null) return null;
        var (count, oldest) = VersionsRetention.Info(_fs, path);
        return new VersionsStatus { Path = path, Folders = count, Oldest = oldest is null ? null : new DateTimeOffset(oldest.Value, TimeZoneInfo.Local.GetUtcOffset(oldest.Value)) };
    }
}
