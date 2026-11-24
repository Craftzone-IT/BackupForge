using System.Globalization;
using BackupForge.Core.Abstractions;
using BackupForge.Core.Config;
using BackupForge.Core.Execution;
using BackupForge.Core.Resources;

namespace BackupForge.Engine.Files;

/// <summary>A restore point of a files job: the current mirror or one Versions stamp folder (SPEC 11.2 / 13.).</summary>
public sealed record RestorePoint(string Id, string Path, DateTime? LocalTime);

/// <summary>Browsing and restoring from the mirror / Versions folders (SPEC 13.). Never writes into the backup target.</summary>
public sealed class FileRestoreEngine(IFileSystem fs, ITargetConnector connector) : IJobEngine
{
    public const string Current = "current";

    public bool CanRun(string jobType, string kind) => jobType == JobTypes.Files && kind == RunKinds.Restore;

    public static List<RestorePoint> Points(IFileSystem fs, BackupConfig cfg, JobConfig job)
    {
        var target = cfg.FindTarget(job.Target!.TargetId)!;
        var list = new List<RestorePoint> { new(Current, JobPaths.MirrorPath(target, job.Target), null) };
        if (job.Files is { Versioning.Enabled: true } f && JobPaths.VersionsPath(target, job.Target, f.Versioning) is { } v)
            foreach (var (path, t) in VersionsRetention.Folders(fs, v).AsEnumerable().Reverse())
                list.Add(new RestorePoint(System.IO.Path.GetFileName(path), path, t));
        return list;
    }

    /// <summary>Resolves a point id + relative path to an absolute path inside that point (no escaping with "..").</summary>
    public static string Resolve(IFileSystem fs, BackupConfig cfg, JobConfig job, string point, string rel)
    {
        var p = Points(fs, cfg, job).FirstOrDefault(x => x.Id == point) ?? throw new InvalidOperationException($"unknown restore point '{point}'");
        var clean = (rel ?? "").Replace('/', '\\').Trim('\\');
        if (clean.Split('\\').Any(seg => seg is ".." or ".")) throw new InvalidOperationException("invalid path");
        return clean.Length == 0 ? p.Path : System.IO.Path.Combine(p.Path, clean.Replace('\\', System.IO.Path.DirectorySeparatorChar));
    }

    public Task<RunOutcome> RunAsync(JobRunContext ctx, CancellationToken ct) => Task.Run(() => Run(ctx, ct), ct);

    private RunOutcome Run(JobRunContext ctx, CancellationToken ct)
    {
        var job = ctx.Job!;
        var p = ctx.Request.Parameters;
        var culture = Texts.CultureFor(ctx.Config.Language);
        var target = ctx.Config.FindTarget(job.Target!.TargetId)!;
        using var _ = connector.Connect(target, ctx.Secrets);
        var rel = (p.GetValueOrDefault("path") ?? "").Replace('/', '\\').Trim('\\');
        var source = Resolve(fs, ctx.Config, job, p.GetValueOrDefault("point") ?? Current, rel);
        var entry = fs.GetEntry(source);
        if (entry is null) return RunOutcome.Failed($"not found in the restore point: {rel}");

        string destRoot;
        string? backupRoot = null;
        var stamp = ctx.Clock.LocalNow().ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture);
        if (p.GetValueOrDefault("to_original") == "1")
        {
            // original place: <source root>\rel; overwritten files go to <source root>_RestoreBackup_<stamp>\rel
            var (srcRoot, innerRel) = OriginalFor(job, rel);
            var inner = innerRel.Replace('\\', System.IO.Path.DirectorySeparatorChar);
            destRoot = inner.Length == 0 ? srcRoot : System.IO.Path.Combine(srcRoot, inner);
            backupRoot = srcRoot.TrimEnd('\\', '/') + "_RestoreBackup_" + stamp.Replace(' ', '_');
            backupRoot = inner.Length == 0 ? backupRoot : System.IO.Path.Combine(backupRoot, inner);
        }
        else
        {
            var to = p.GetValueOrDefault("target") ?? throw new InvalidOperationException("restore target missing");
            if (fs.DirectoryExists(to) && fs.Enumerate(to).Any()) return RunOutcome.Failed($"restore target is not empty: {to}");
            destRoot = rel.Length == 0 ? to : System.IO.Path.Combine(to, System.IO.Path.GetFileName(source));
        }

        var outcome = new RunOutcome();
        var copier = new FileCopier(fs, null);
        void CopyOne(FsEntry f, string dest)
        {
            ct.ThrowIfCancellationRequested();
            fs.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
            Action<string>? moveAway = null;
            if (backupRoot is not null && fs.FileExists(dest))
            {
                var relDest = System.IO.Path.GetRelativePath(destRoot, dest);
                var b = entry!.IsDirectory ? System.IO.Path.Combine(backupRoot, relDest) : backupRoot;
                moveAway = old =>
                {
                    fs.CreateDirectory(System.IO.Path.GetDirectoryName(b)!);
                    fs.MoveFile(old, b, overwrite: true);
                };
            }
            try
            {
                outcome.BytesTransferred += copier.Copy(f, dest, moveAway, null, ct);
                outcome.FilesCopied++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                outcome.AddError(dest, ex.Message);
                ctx.Log.Error($"{dest}: {ex.Message}");
            }
        }

        if (!entry.IsDirectory) CopyOne(entry, destRoot);
        else
        {
            var stack = new Stack<(string Src, string Dst)>();
            stack.Push((source, destRoot));
            while (stack.Count > 0)
            {
                var (s, d) = stack.Pop();
                fs.CreateDirectory(d);
                foreach (var e in fs.Enumerate(s))
                {
                    if (e.IsReparsePoint || e.Name.EndsWith(FileCopier.PartSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                    var dd = System.IO.Path.Combine(d, e.Name);
                    if (e.IsDirectory) stack.Push((e.FullPath, dd));
                    else CopyOne(e, dd);
                }
            }
        }
        outcome.Result = outcome.ErrorCount == 0 ? RunResults.Ok : outcome.FilesCopied > 0 ? RunResults.Warning : RunResults.Failed;
        outcome.Summary = Texts.Format(culture, "restic.restore", outcome.FilesCopied, SizeText.Format(outcome.BytesTransferred, culture), destRoot)
                          + (backupRoot is null ? "" : $" · {backupRoot}");
        return outcome;
    }

    /// <summary>The original location of a mirror-relative path: single source → its root; several → by the first segment.</summary>
    public static (string Root, string Inner) OriginalFor(JobConfig job, string rel)
    {
        if (job.Sources.Count == 1) return (job.Sources[0].Path, rel);
        var first = rel.Split('\\')[0];
        var src = job.Sources.FirstOrDefault(s => string.Equals(System.IO.Path.GetFileName(s.Path.TrimEnd('\\', '/')), first, StringComparison.OrdinalIgnoreCase))
                  ?? throw new InvalidOperationException("choose a folder inside one of the sources");
        return (src.Path, rel.Length > first.Length ? rel[(first.Length + 1)..] : "");
    }
}
