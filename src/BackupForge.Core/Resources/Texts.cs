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
