using System.Windows.Markup;
using BackupForge.Gui.Core;

namespace BackupForge.Gui;

/// <summary>{l:Loc key} — a GUI string in the configured language.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;
    public override object ProvideValue(IServiceProvider serviceProvider) => Ui.T(Key);
}
