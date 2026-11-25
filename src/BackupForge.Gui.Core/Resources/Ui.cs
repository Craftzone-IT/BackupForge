using System.Globalization;
using System.Resources;

namespace BackupForge.Gui.Core;

/// <summary>GUI strings (UiStrings.resx = English, UiStrings.hu.resx = Hungarian).</summary>
public static class Ui
{
    private static readonly ResourceManager Manager = new("BackupForge.Gui.Core.Resources.UiStrings", typeof(Ui).Assembly);
    public static CultureInfo Culture { get; set; } = CultureInfo.GetCultureInfo("hu-HU");
    public static string T(string key) => Manager.GetString(key, Culture) ?? key;
    public static string F(string key, params object?[] args) => string.Format(Culture, T(key), args);
}
