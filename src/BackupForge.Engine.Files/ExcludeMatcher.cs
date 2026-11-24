using System.Text;
using System.Text.RegularExpressions;

namespace BackupForge.Engine.Files;

/// <summary>
/// Exclusion patterns (SPEC 6.7). Relative paths use '\' and no leading separator.
/// <list type="bullet">
/// <item><c>*.tmp</c>, <c>~$*</c>, <c>Thumbs.db</c> — no separator: matches the file or folder NAME at any depth.</item>
/// <item><c>\$RECYCLE.BIN\</c>, <c>\System Volume Information\</c> — leading and trailing '\': a FOLDER name at any depth.</item>
/// <item><c>\Projects\old\</c> — several segments between separators: that folder path from the source root.</item>
/// <item><c>logs\*.log</c>, <c>**\bin\*.dll</c> — with a separator: the relative path from the root; <c>**</c> spans folders.</item>
/// </list>
/// Matching is case-insensitive (Windows semantics).
/// </summary>
public sealed class ExcludeMatcher
{
    public static readonly string[] DefaultExcludes =
        ["pagefile.sys", "hiberfil.sys", "swapfile.sys", @"\System Volume Information\", @"\$RECYCLE.BIN\", "*.bfpart", "~$*", "Thumbs.db"];

    private readonly List<Regex> _nameRules = [];      // file or folder name
    private readonly List<Regex> _folderNameRules = []; // folder name only
    private readonly List<Regex> _pathRules = [];      // relative path (file or folder)
    private readonly List<Regex> _folderPathRules = []; // relative folder path

    public ExcludeMatcher(IEnumerable<string> patterns)
    {
        foreach (var raw in patterns)
        {
            var p = raw.Trim().Replace('/', '\\');
            if (p.Length == 0) continue;
            var folderOnly = p.EndsWith('\\');
            var anchored = p.StartsWith('\\');
            var core = p.Trim('\\');
            if (core.Length == 0) continue;
            var hasSep = core.Contains('\\');
            if (!hasSep && (!anchored || folderOnly))
            {
                // "\name\" = folder name anywhere; "name" = any name anywhere
                (folderOnly ? _folderNameRules : _nameRules).Add(ToRegex(core));
            }
            else
            {
                (folderOnly ? _folderPathRules : _pathRules).Add(ToRegex(core));
            }
        }
    }

    public int Count => _nameRules.Count + _folderNameRules.Count + _pathRules.Count + _folderPathRules.Count;

    public static ExcludeMatcher For(IEnumerable<string> jobExcludes, bool useDefaults) =>
        new(useDefaults ? DefaultExcludes.Concat(jobExcludes) : jobExcludes);

    public bool IsExcluded(string relativePath, bool isDirectory)
    {
        var name = relativePath.Contains('\\') ? relativePath[(relativePath.LastIndexOf('\\') + 1)..] : relativePath;
        foreach (var r in _nameRules) if (r.IsMatch(name)) return true;
        foreach (var r in _pathRules) if (r.IsMatch(relativePath)) return true;
        if (isDirectory)
        {
            foreach (var r in _folderNameRules) if (r.IsMatch(name)) return true;
            foreach (var r in _folderPathRules) if (r.IsMatch(relativePath)) return true;
        }
        return false;
    }

    private static Regex ToRegex(string glob)
    {
        var sb = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                i++;
                if (i + 1 < glob.Length && glob[i + 1] == '\\') { i++; sb.Append(@"(?:.*\\)?"); }
                else sb.Append(".*");
            }
            else if (c == '*') sb.Append(@"[^\\]*");
            else if (c == '?') sb.Append(@"[^\\]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }
}
