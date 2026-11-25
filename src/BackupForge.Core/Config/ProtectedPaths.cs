using BackupForge.Core.Abstractions;
using BackupForge.Core.Execution;

namespace BackupForge.Core.Config;

public static class PathRoles
{
    public const string Mirror = "mirror";
    public const string Versions = "versions";
    public const string Source = "source";
    public const string Repository = "restic_repo";
    public const string Staging = "staging";
    public const string DataRoot = "data_root";
}

/// <summary>A folder that belongs to one job (or to BackupForge itself, JobId null) and must not be touched by another job.</summary>
public sealed record ProtectedPath(string Path, string? JobId, string Role);

/// <summary>
/// CR-001 1.: every folder a job owns (mirror, Versions, source, local restic repository, SQL staging) plus the data folder.
/// One code path for the validator (1a), the files engine safety net (1b) and the resource locks (1c).
/// </summary>
public static class ProtectedPaths
{
    public static List<ProtectedPath> Collect(BackupConfig config, string dataRoot)
    {
        var list = new List<ProtectedPath>();
        var paths = new DataPaths(dataRoot);
        foreach (var j in config.Jobs)
        {
            if (j.Type == JobTypes.Files && j.Target is not null && config.FindTarget(j.Target.TargetId) is { Path.Length: > 0 } t)
            {
                list.Add(new ProtectedPath(JobPaths.MirrorPath(t, j.Target), j.Id, PathRoles.Mirror));
                if (j.Files is { Versioning.Enabled: true } f && JobPaths.VersionsPath(t, j.Target, f.Versioning) is { } v)
                    list.Add(new ProtectedPath(v, j.Id, PathRoles.Versions));
            }
            if (j.Type is JobTypes.Files or JobTypes.Restic)
                foreach (var s in j.Sources.Where(s => !string.IsNullOrWhiteSpace(s.Path)))
                {
                    var src = s.Path.StartsWith(ConfigValidator.StagingPrefix, StringComparison.Ordinal)
                        ? StagingPaths.For(config, paths, s.Path[ConfigValidator.StagingPrefix.Length..])
                        : s.Path;
                    list.Add(new ProtectedPath(src, j.Id, PathRoles.Source));
                }
            if (j.Type == JobTypes.Restic && j.Restic is not null && config.FindRepo(j.Restic.RepoId) is { } repo && LocalRepoPath(repo.Location) is { } rp)
                list.Add(new ProtectedPath(rp, j.Id, PathRoles.Repository));
            if (j.Type == JobTypes.Sql && j.Sql is not null)
                list.Add(new ProtectedPath(StagingPaths.For(config, paths, j.Id), j.Id, PathRoles.Staging));
        }
        // repositories not (yet) used by a job are still somebody's data
        foreach (var repo in config.ResticRepos)
            if (LocalRepoPath(repo.Location) is { } rp && !list.Any(p => p.Role == PathRoles.Repository && PathUtil.EqualsPath(p.Path, rp)))
                list.Add(new ProtectedPath(rp, "repo:" + repo.Id, PathRoles.Repository));
        list.Add(new ProtectedPath(dataRoot, null, PathRoles.DataRoot));
        return list;
    }

    /// <summary>A restic location that is a file-system path (local or UNC); null for sftp:, s3:, rest:, … backends.</summary>
    public static string? LocalRepoPath(string location)
    {
        if (string.IsNullOrWhiteSpace(location)) return null;
        var l = location.StartsWith("local:", StringComparison.OrdinalIgnoreCase) ? location[6..] : location;
        return PathUtil.IsAbsolute(l) ? l : null;
    }

    /// <summary>Paths of other jobs (or BackupForge's data) that <paramref name="path"/> equals, contains or lies in.</summary>
    public static IEnumerable<ProtectedPath> Conflicts(IEnumerable<ProtectedPath> all, string jobId, string path) =>
        all.Where(p => p.JobId != jobId && PathUtil.Overlaps(path, p.Path));

    /// <summary>The protected path (of another job) an absolute path to be deleted lies in, if any.</summary>
    public static ProtectedPath? Owner(IEnumerable<ProtectedPath> all, string jobId, string absolutePath) =>
        all.FirstOrDefault(p => p.JobId != jobId && PathUtil.IsSameOrUnder(absolutePath, p.Path));
}
