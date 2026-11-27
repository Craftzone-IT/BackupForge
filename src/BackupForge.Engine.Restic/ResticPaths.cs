namespace BackupForge.Engine.Restic;

/// <summary>Paths inside a restic snapshot (CR-002 A4.): restic stores C:\data\x as /C/data/x.</summary>
public static class ResticPaths
{
    /// <summary>
    /// "C:\data\x", "C:/data/x", "c:\data\x\" → "/C/data/x"; "/C/data/x" and Unix paths stay; empty → null.
    /// The one conversion for the CLI (<c>bf restore --path</c>) and the GUI (both go through <c>restore.run</c> / <c>restore.browse</c>).
    /// </summary>
    public static string? ToSnapshotPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim().Replace('\\', '/');
        if (p.Length >= 2 && char.IsAsciiLetter(p[0]) && p[1] == ':') p = "/" + char.ToUpperInvariant(p[0]) + p[2..];
        if (!p.StartsWith('/')) p = "/" + p;
        while (p.Contains("//", StringComparison.Ordinal)) p = p.Replace("//", "/", StringComparison.Ordinal);
        if (p.Length > 1) p = p.TrimEnd('/');
        return p;
    }
}
