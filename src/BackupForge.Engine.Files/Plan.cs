using BackupForge.Core.Abstractions;

namespace BackupForge.Engine.Files;

public enum PlanAction
{
    CreateDir,
    CopyNew,
    Update,
    DeleteFile,
    DeleteDir,
    RenameCase,
    TouchTime,
}

/// <summary>One step of a run. <see cref="Rel"/> is relative to the mirror root ('\' separated).</summary>
public sealed record PlanItem(PlanAction Action, string Rel, long Size = 0, string? SourcePath = null, string? TargetName = null, FsEntry? Source = null);

public sealed class FilePlan
{
    public List<PlanItem> Items { get; } = [];
    /// <summary>Files found in the mirror (not excluded) — the base of the delete-guard percentage.</summary>
    public long TargetFileCount { get; set; }
    public long SourceFileCount { get; set; }
    public long SourceBytes { get; set; }
    public List<(string Path, string Message)> Warnings { get; } = [];
    public List<string> Infos { get; } = [];
    public List<string> CloudOnly { get; } = [];
    public bool DstShiftLogged { get; set; }

    public IEnumerable<PlanItem> Copies => Items.Where(i => i.Action is PlanAction.CopyNew or PlanAction.Update);
    public long CopyBytes => Copies.Sum(i => i.Size);
    public int CopyCount => Items.Count(i => i.Action == PlanAction.CopyNew);
    public int UpdateCount => Items.Count(i => i.Action == PlanAction.Update);
    /// <summary>Files that disappear from the mirror (single deletes + files inside deleted folders).</summary>
    public long DeleteFileCount => Items.Where(i => i.Action == PlanAction.DeleteFile).LongCount();

    public void RemoveDeletes() => Items.RemoveAll(i => i.Action is PlanAction.DeleteFile or PlanAction.DeleteDir);
}

/// <summary>A source root and the folder of the mirror it goes to ("" = the mirror root).</summary>
public sealed record SourceRoot(string OriginalPath, string ReadPath, string MirrorSubdir);

/// <summary>
/// Walks sources and the mirror and decides per file what to do (SPEC 6.1, 6.2, 6.8). Never changes anything.
/// Safety: a source folder that cannot be listed never causes deletions in its mirror counterpart.
/// </summary>
public sealed class Planner
{
    private readonly IFileSystem _fs;
    private readonly ExcludeMatcher _excludes;
    private readonly bool _mirror;
    private readonly bool _contentCompare;
    private readonly bool _skipCloud;
    private readonly StringComparer _names;
    private readonly CancellationToken _ct;
    private readonly FilePlan _plan = new();

    public Planner(IFileSystem fs, ExcludeMatcher excludes, bool mirror, bool contentCompare, bool skipCloud, bool caseInsensitive, CancellationToken ct)
    {
        _fs = fs;
        _excludes = excludes;
        _mirror = mirror;
        _contentCompare = contentCompare;
        _skipCloud = skipCloud;
        _names = caseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        _ct = ct;
    }

    public FilePlan Build(IReadOnlyList<SourceRoot> sources, string mirrorRoot)
    {
        if (sources.Count == 1 && sources[0].MirrorSubdir.Length == 0)
        {
            WalkPair(sources[0].ReadPath, _fs.DirectoryExists(mirrorRoot) ? mirrorRoot : null, "", isSourceRoot: true);
            return _plan;
        }

        // several sources: each into its own subfolder; other mirror-root entries are foreign to the job only
        // if they are not one of the source folders → in mirror mode they are removed like any extra entry.
        var dstEntries = _fs.DirectoryExists(mirrorRoot) ? List(mirrorRoot, "", isSource: false) : null;
        var wanted = new HashSet<string>(sources.Select(s => s.MirrorSubdir), _names);
        foreach (var s in sources)
        {
            var dstPath = Path.Combine(mirrorRoot, s.MirrorSubdir);
            var existing = dstEntries?.FirstOrDefault(e => _names.Equals(e.Name, s.MirrorSubdir));
            if (existing is null) _plan.Items.Add(new PlanItem(PlanAction.CreateDir, s.MirrorSubdir));
            else if (!existing.IsDirectory)
            {
                if (_mirror) _plan.Items.Add(new PlanItem(PlanAction.DeleteFile, s.MirrorSubdir, existing.Length));
                _plan.Items.Add(new PlanItem(PlanAction.CreateDir, s.MirrorSubdir));
                existing = null;
            }
            WalkPair(s.ReadPath, existing is null ? null : dstPath, s.MirrorSubdir, isSourceRoot: true);
        }
        if (_mirror && dstEntries is not null)
            foreach (var e in dstEntries.Where(e => !wanted.Contains(e.Name)))
                AddDelete(e, e.Name);
        return _plan;
    }

    private List<FsEntry>? List(string dir, string rel, bool isSource)
    {
        try
        {
            var list = new List<FsEntry>();
            foreach (var e in _fs.Enumerate(dir))
            {
                var childRel = rel.Length == 0 ? e.Name : rel + "\\" + e.Name;
                if (_excludes.IsExcluded(childRel, e.IsDirectory)) continue;
                if (!isSource && !e.IsDirectory && e.Name.EndsWith(FileCopier.PartSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                if (e.IsReparsePoint)
                {
                    if (isSource) _plan.Infos.Add($"reparse point not followed: {e.FullPath}");
                    continue; // never follow and never delete links
                }
                list.Add(e);
            }
            return list;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            if (isSource) _plan.Warnings.Add((dir, "cannot list folder: " + ex.Message));
            else _plan.Warnings.Add((dir, "cannot list target folder: " + ex.Message));
            return null;
        }
    }

    private void WalkPair(string srcDir, string? dstDir, string rel, bool isSourceRoot)
    {
        var stack = new Stack<(string Src, string? Dst, string Rel)>();
        stack.Push((srcDir, dstDir, rel));
        while (stack.Count > 0)
        {
            _ct.ThrowIfCancellationRequested();
            var (s, d, r) = stack.Pop();
            var src = List(s, r, isSource: true);
            if (src is null) continue; // unknown source state → touch nothing below
            var dst = d is null ? [] : List(d, r, isSource: false);
            if (dst is null) continue;

            var dstByName = new Dictionary<string, FsEntry>(_names);
            foreach (var e in dst) dstByName.TryAdd(e.Name, e);
            var seen = new HashSet<string>(_names);

            foreach (var se in src)
            {
                var childRel = r.Length == 0 ? se.Name : r + "\\" + se.Name;
                seen.Add(se.Name);
                dstByName.TryGetValue(se.Name, out var de);
                if (de is not null && !string.Equals(de.Name, se.Name, StringComparison.Ordinal))
                    _plan.Items.Add(new PlanItem(PlanAction.RenameCase, r.Length == 0 ? de.Name : r + "\\" + de.Name, TargetName: se.Name));

                if (se.IsDirectory)
                {
                    if (de is { IsDirectory: false })
                    {
                        if (!_mirror) { _plan.Warnings.Add((childRel, "a file in the target has the name of a source folder (update mode: skipped)")); continue; }
                        _plan.Items.Add(new PlanItem(PlanAction.DeleteFile, childRel, de.Length));
                        de = null;
                    }
                    if (de is null) _plan.Items.Add(new PlanItem(PlanAction.CreateDir, childRel));
                    stack.Push((se.FullPath, de?.FullPath, childRel));
                    continue;
                }

                if (se.IsCloudOnly)
                {
                    _plan.CloudOnly.Add(se.FullPath);
                    if (_skipCloud) _plan.Warnings.Add((se.FullPath, "cloud-only (OneDrive) file skipped"));
                    continue;
                }

                _plan.SourceFileCount++;
                _plan.SourceBytes += se.Length;

                if (de is { IsDirectory: true })
                {
                    if (!_mirror) { _plan.Warnings.Add((childRel, "a folder in the target has the name of a source file (update mode: skipped)")); continue; }
                    AddDelete(de, childRel);
                    de = null;
                }
                if (de is null)
                {
                    _plan.Items.Add(new PlanItem(PlanAction.CopyNew, childRel, se.Length, se.FullPath, Source: se));
                    continue;
                }
                switch (Comparison.Compare(se, de, _contentCompare))
                {
                    case CompareVerdict.Same:
                        break;
                    case CompareVerdict.SameWithDstShift:
                        if (!_plan.DstShiftLogged)
                        {
                            _plan.DstShiftLogged = true;
                            _plan.Infos.Add($"timestamps differ by exactly 1 hour (DST / FAT target), treated as unchanged: {childRel}");
                        }
                        break;
                    case CompareVerdict.NeedsContentCheck:
                        if (Comparison.Hash(_fs, se.FullPath, _ct) == Comparison.Hash(_fs, de.FullPath, _ct))
                            _plan.Items.Add(new PlanItem(PlanAction.TouchTime, childRel, se.Length, se.FullPath, Source: se));
                        else
                            _plan.Items.Add(new PlanItem(PlanAction.Update, childRel, se.Length, se.FullPath, Source: se));
                        break;
                    default:
                        _plan.Items.Add(new PlanItem(PlanAction.Update, childRel, se.Length, se.FullPath, Source: se));
                        break;
                }
            }

            foreach (var de in dst)
            {
                if (!de.IsDirectory) _plan.TargetFileCount++;
                if (seen.Contains(de.Name)) continue;
                if (!_mirror)
                {
                    if (de.IsDirectory) CountTargetFiles(de.FullPath, r.Length == 0 ? de.Name : r + "\\" + de.Name);
                    continue;
                }
                AddDelete(de, r.Length == 0 ? de.Name : r + "\\" + de.Name);
            }
        }
    }

    private void CountTargetFiles(string dir, string rel)
    {
        var list = List(dir, rel, isSource: false);
        if (list is null) return;
        foreach (var e in list)
        {
            if (e.IsDirectory) CountTargetFiles(e.FullPath, rel + "\\" + e.Name);
            else _plan.TargetFileCount++;
        }
    }

    /// <summary>A mirror entry without source counterpart: files one by one (so they can be versioned), then the folders.</summary>
    private void AddDelete(FsEntry e, string rel)
    {
        if (!e.IsDirectory)
        {
            _plan.Items.Add(new PlanItem(PlanAction.DeleteFile, rel, e.Length));
            return;
        }
        var list = List(e.FullPath, rel, isSource: false);
        if (list is null) return; // cannot list → do not delete the folder
        foreach (var c in list)
        {
            var childRel = rel + "\\" + c.Name;
            if (c.IsDirectory) AddDelete(c, childRel);
            else
            {
                _plan.TargetFileCount++;
                _plan.Items.Add(new PlanItem(PlanAction.DeleteFile, childRel, c.Length));
            }
        }
        _plan.Items.Add(new PlanItem(PlanAction.DeleteDir, rel));
    }
}
