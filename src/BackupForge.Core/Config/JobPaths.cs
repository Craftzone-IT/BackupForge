using BackupForge.Core.Abstractions;

namespace BackupForge.Core.Config;

/// <summary>Resolves where a files job writes (SPEC 6.4).</summary>
public static class JobPaths
{
    public const string VersionsSuffix = "_Versions";

    public static string MirrorPath(TargetConfig target, JobTargetRef jobTarget) =>
        string.IsNullOrWhiteSpace(jobTarget.Subdir) ? target.Path : PathUtil.Combine(target.Path, jobTarget.Subdir);

    /// <summary>
    /// The Versions folder: next to the mirror folder (<c>&lt;target&gt;\&lt;subdir&gt;_Versions</c>) unless
    /// <c>versioning.folder</c> overrides it (absolute, or relative to the target root). Null if it cannot be resolved.
    /// </summary>
    public static string? VersionsPath(TargetConfig target, JobTargetRef jobTarget, VersioningOptions versioning)
    {
        if (!string.IsNullOrWhiteSpace(versioning.Folder))
            return PathUtil.IsAbsolute(versioning.Folder) ? versioning.Folder : PathUtil.Combine(target.Path, versioning.Folder);
        if (string.IsNullOrWhiteSpace(jobTarget.Subdir)) return null;
        var mirror = MirrorPath(target, jobTarget).TrimEnd('\\', '/');
        return mirror + VersionsSuffix;
    }
}
