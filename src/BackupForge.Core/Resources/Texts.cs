using System.Globalization;
using System.Resources;

namespace BackupForge.Core.Resources;

/// <summary>User-facing strings (Strings.resx = English, Strings.hu.resx = Hungarian).</summary>
public static class Texts
{
    private static readonly ResourceManager Manager = new("BackupForge.Core.Resources.Strings", typeof(Texts).Assembly);

    public static CultureInfo Hungarian { get; } = CultureInfo.GetCultureInfo("hu-HU");
    public static CultureInfo English { get; } = CultureInfo.GetCultureInfo("en-US");

    public static CultureInfo CultureFor(string? language) =>
        string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? English : Hungarian;

    public static string Get(string key, CultureInfo culture) => Manager.GetString(key, culture) ?? key;

    public static string Format(CultureInfo culture, string key, params object?[] args)
    {
        var fmt = Get(key, culture);
        return args.Length == 0 ? fmt : string.Format(culture, fmt, args);
    }
}

public static class SizeText
{
    public static string Format(long bytes, CultureInfo culture)
    {
        double b = bytes;
        if (b < 1024) return Texts.Format(culture, "size.b", bytes);
        if (b < 1024 * 1024) return Texts.Format(culture, "size.kb", b / 1024);
        if (b < 1024.0 * 1024 * 1024) return Texts.Format(culture, "size.mb", b / 1024 / 1024);
        if (b < 1024.0 * 1024 * 1024 * 1024) return Texts.Format(culture, "size.gb", b / 1024 / 1024 / 1024);
        return Texts.Format(culture, "size.tb", b / 1024 / 1024 / 1024 / 1024);
    }
}

/// <summary>Minimal HTML escaping for UTF-8 mails (WebUtility.HtmlEncode would turn every 'é' into an entity).</summary>
public static class Html
{
    public static string Encode(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder(s.Length + 16);
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&#39;"); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }
}
