using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using BackupForge.Gui.Core;
using Microsoft.Win32;

namespace BackupForge.Gui;

public sealed partial class WpfDialogs : IUserDialogs
{
    /// <summary>The window of a dialog ViewModel (CR-002 B2./B3.).</summary>
    public bool ShowDialog(object viewModel)
    {
        Window w = viewModel switch
        {
            Gui.Core.Wizard.JobWizardViewModel vm => new JobWizardWindow(vm),
            Gui.Core.Wizard.NewTargetViewModel vm => new NewTargetWindow(vm),
            Gui.Core.ViewModels.NewRepoViewModel vm => new NewRepoWindow(vm),
            _ => throw new ArgumentException("no window for " + viewModel.GetType().Name),
        };
        w.Owner = Application.Current.Windows.OfType<Window>().LastOrDefault(x => x.IsActive) ?? Application.Current.MainWindow;
        return w.ShowDialog() == true;
    }

    /// <summary>Z:\a\b → \\server\share\a\b for a mapped drive of the logged-on user (WNetGetConnection), else null.</summary>
    public string? NetworkPathFor(string path)
    {
        if (path.Length < 2 || path[1] != ':') return null;
        var buffer = new System.Text.StringBuilder(512);
        var len = buffer.Capacity;
        return WNetGetConnection(path[..2], buffer, ref len) == 0 ? buffer.ToString().TrimEnd('\\') + path[2..] : null;
    }

    [System.Runtime.InteropServices.DllImport("mpr.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "WNetGetConnectionW")]
    private static extern int WNetGetConnection(string localName, System.Text.StringBuilder remoteName, ref int length);

    public bool Confirm(string message) => MessageBox.Show(message, "BackupForge", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
    public void Info(string message) => MessageBox.Show(message, "BackupForge", MessageBoxButton.OK, MessageBoxImage.Information);
    public void Error(string message) => MessageBox.Show(message, "BackupForge", MessageBoxButton.OK, MessageBoxImage.Error);

    public string? PickFolder(string? start)
    {
        var d = new OpenFolderDialog { InitialDirectory = start ?? "" };
        return d.ShowDialog() == true ? d.FolderName : null;
    }

    public string? SaveFile(string defaultName, string filter)
    {
        var d = new SaveFileDialog { FileName = defaultName, Filter = filter };
        return d.ShowDialog() == true ? d.FileName : null;
    }

    public string? OpenFile(string filter)
    {
        var d = new OpenFileDialog { Filter = filter };
        return d.ShowDialog() == true ? d.FileName : null;
    }

    /// <summary>Shows a text (e.g. the recovery sheet) with Print and Save buttons.</summary>
    public void ShowText(string title, string text, bool printable)
    {
        var box = new TextBox { Text = text, IsReadOnly = true, FontFamily = new System.Windows.Media.FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var print = new Button { Content = Ui.T("l_print"), Visibility = printable ? Visibility.Visible : Visibility.Collapsed };
        var save = new Button { Content = Ui.T("l_save_file") };
        var close = new Button { Content = "OK", IsDefault = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { print, save, close } };
        var dock = new DockPanel { Margin = new Thickness(8) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(buttons);
        dock.Children.Add(box);
        var w = new Window { Title = title, Content = dock, Width = 640, Height = 420, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Application.Current.MainWindow };
        close.Click += (_, _) => w.Close();
        save.Click += (_, _) => { if (SaveFile("backupforge.txt", "Text|*.txt") is { } p) System.IO.File.WriteAllText(p, text); };
        print.Click += (_, _) =>
        {
            var pd = new PrintDialog();
            if (pd.ShowDialog() != true) return;
            var doc = new FlowDocument(new Paragraph(new Run(text))) { FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 13, PagePadding = new Thickness(60) };
            pd.PrintDocument(((IDocumentPaginatorSource)doc).DocumentPaginator, title);
        };
        w.ShowDialog();
    }
}
