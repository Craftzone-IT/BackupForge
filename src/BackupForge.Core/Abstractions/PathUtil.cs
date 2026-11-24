namespace BackupForge.Core.Abstractions;

/// <summary>
/// String-level path helpers that work on Windows-style config paths on any OS (validation and
/// planning must give the same answer on the Linux CI as on the target server).
/// </summary>
public static class PathUtil
{
    public static char SeparatorFor(string basePath) =>
        basePath.Contains('\\') || (basePath.Length >= 2 && basePath[1] == ':') ? '\\' : '/';

    /// <summary>Normalizes separators to '\', collapses duplicates (keeping a leading UNC '\\'), trims the trailing one.</summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var p = path.Trim().Replace('/', '\\');
        var unc = p.StartsWith(@"\\", StringComparison.Ordinal);
        var sb = new System.Text.StringBuilder(p.Length);
        for (var i = 0; i < p.Length; i++)
        {
            if (p[i] == '\\' && sb.Length > 0 && sb[^1] == '\\' && !(unc && i == 1)) continue;
            sb.Append(p[i]);
        }
        var s = sb.ToString();
        while (s.Length > 1 && s.EndsWith('\\') && !(s.Length == 3 && s[1] == ':')) s = s[..^1];
        if (s.Length == 2 && s[1] == ':') s += "\\";
        return s;
    }

    public static bool EqualsPath(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>True if <paramref name="inner"/> equals or lies below <paramref name="outer"/>.</summary>
    public static bool IsSameOrUnder(string inner, string outer)
    {
        var i = Normalize(inner);
        var o = Normalize(outer);
        if (i.Length == 0 || o.Length == 0) return false;
        if (string.Equals(i, o, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = o.EndsWith('\\') ? o : o + "\\";
        return i.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool Overlaps(string a, string b) => IsSameOrUnder(a, b) || IsSameOrUnder(b, a);

    /// <summary>Joins using the separator style of <paramref name="basePath"/>.</summary>
    public static string Combine(string basePath, params string[] parts)
    {
        var sep = SeparatorFor(basePath);
        var result = basePath;
        foreach (var raw in parts)
        {
            if (string.IsNullOrEmpty(raw)) continue;
            var part = raw.Replace(sep == '\\' ? '/' : '\\', sep).Trim(sep);
            if (part.Length == 0) continue;
            result = result.EndsWith(sep) ? result + part : result + sep + part;
        }
        return result;
    }

    public static bool IsAbsolute(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) ||
        (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/')) ||
        path.StartsWith('/');

    public static bool IsUnc(string path) => path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal);
}
