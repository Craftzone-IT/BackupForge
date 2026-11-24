using System.Globalization;
using System.Text.RegularExpressions;
using BackupForge.Core.Abstractions;

namespace BackupForge.Engine.Files;

/// <summary>
/// Versions folder layout and retention (SPEC 6.4): &lt;Versions&gt;\yyyy-MM-dd HHmmss\&lt;relative path&gt;.
/// Only folders whose NAME matches the stamp pattern, directly inside the job's own Versions folder, are ever deleted.
/// </summary>
public static partial class VersionsRetention
{
    public const string StampFormat = "yyyy-MM-dd HHmmss";

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2} \d{6}$")]
    private static partial Regex StampRegex();

    public static string Stamp(DateTimeOffset localTime) => localTime.ToString(StampFormat, CultureInfo.InvariantCulture);

    public static bool TryParseStamp(string name, out DateTime local) =>
        DateTime.TryParseExact(name, StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out local) & StampRegex().IsMatch(name);

    public sealed record Result(int FoldersDeleted, int FilesDeleted, List<string> Errors);

    /// <summary>The version folders (stamp-named) of a Versions root, oldest first.</summary>
    public static List<(string Path, DateTime Local)> Folders(IFileSystem fs, string versionsRoot)
    {
        var list = new List<(string, DateTime)>();
        if (!fs.DirectoryExists(versionsRoot)) return list;
        foreach (var e in fs.Enumerate(versionsRoot))
            if (e.IsDirectory && !e.IsReparsePoint && TryParseStamp(e.Name, out var t)) list.Add((e.FullPath, t));
        list.Sort((a, b) => a.Item2.CompareTo(b.Item2));
        return list;
    }

    public static Result Apply(IFileSystem fs, string versionsRoot, int keepDays, int? maxVersionsPerFile, DateTime localNow, CancellationToken ct)
    {
        var errors = new List<string>();
        var foldersDeleted = 0;
        var filesDeleted = 0;
        var folders = Folders(fs, versionsRoot);
        var cutoff = localNow.AddDays(-keepDays);
        foreach (var (path, t) in folders.Where(f => f.Local < cutoff).ToList())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                fs.DeleteDirectory(path, recursive: true);
                foldersDeleted++;
                folders.RemoveAll(f => f.Path == path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{path}: {ex.Message}");
            }
        }

        if (maxVersionsPerFile is { } max && max > 0)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, _) in folders.AsEnumerable().Reverse()) // newest first
            {
                ct.ThrowIfCancellationRequested();
                foreach (var (file, rel) in Files(fs, path, ""))
                {
                    var n = counts[rel] = counts.GetValueOrDefault(rel) + 1;
                    if (n <= max) continue;
                    try { fs.DeleteFile(file); filesDeleted++; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add($"{file}: {ex.Message}"); }
                }
                RemoveEmptyDirs(fs, path, isRoot: true);
            }
        }
        return new Result(foldersDeleted, filesDeleted, errors);
    }

    private static IEnumerable<(string Full, string Rel)> Files(IFileSystem fs, string dir, string rel)
    {
        List<FsEntry> entries;
        try { entries = [.. fs.Enumerate(dir)]; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var e in entries)
        {
            var r = rel.Length == 0 ? e.Name : rel + "\\" + e.Name;
            if (e.IsReparsePoint) continue;
            if (e.IsDirectory) foreach (var x in Files(fs, e.FullPath, r)) yield return x;
            else yield return (e.FullPath, r);
        }
    }

    private static bool RemoveEmptyDirs(IFileSystem fs, string dir, bool isRoot)
    {
        var empty = true;
        foreach (var e in fs.Enumerate(dir).ToList())
        {
            if (e.IsDirectory && !e.IsReparsePoint) { if (!RemoveEmptyDirs(fs, e.FullPath, false)) empty = false; }
            else empty = false;
        }
        if (empty)
        {
            try { fs.DeleteDirectory(dir, recursive: false); } catch (IOException) { return false; }
        }
        return empty;
    }

    /// <summary>Count, oldest and (optionally) size of the Versions folder for the status.</summary>
    public static (int Folders, DateTime? Oldest) Info(IFileSystem fs, string versionsRoot)
    {
        try
        {
            var f = Folders(fs, versionsRoot);
            return (f.Count, f.Count == 0 ? null : f[0].Local);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (0, null); }
    }
}
