using System.Globalization;

namespace BackupForge.Core.Execution;

/// <summary>CR-002 A5.: the generated restore folder never collides with an existing / just-reserved one.</summary>
public static class RestoreTargets
{
    public static string Stamp(DateTimeOffset localNow) => localNow.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture);

    /// <summary><paramref name="baseName"/>, else "<paramref name="baseName"/> (2)", "(3)", … — the first that is not <paramref name="taken"/>.</summary>
    public static string Unique(string parent, string baseName, Func<string, bool> taken)
    {
        var path = Path.Combine(parent, baseName);
        for (var i = 2; taken(path); i++) path = Path.Combine(parent, $"{baseName} ({i})");
        return path;
    }

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// &lt;to&gt;\Restore_&lt;stamp&gt; for a new restore. Two restores queued in the same second both get here before either
    /// folder exists, so the chosen name is also reserved in memory (process-wide).
    /// </summary>
    public static string NewRestoreFolder(string to, DateTimeOffset localNow, Func<string, bool> exists)
    {
        lock (Reserved)
        {
            var path = Unique(to, "Restore_" + Stamp(localNow), p => Reserved.Contains(p) || exists(p));
            Reserved.Add(path);
            return path;
        }
    }
}
