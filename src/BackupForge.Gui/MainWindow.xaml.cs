using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using BackupForge.Gui.Core.ViewModels;

namespace BackupForge.Gui;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private MainViewModel Vm => (MainViewModel)DataContext;

    // PasswordBox.Password is not bindable on purpose: the value goes straight to secrets.set and the box is cleared.
    private void SetTargetPassword(object sender, RoutedEventArgs e) { Vm.Targets.SelectedTarget?.SetPasswordCommand.Execute(TargetPw.Password); TargetPw.Clear(); }
    private void SetRepoPassword(object sender, RoutedEventArgs e) { Vm.Targets.SelectedRepo?.SetPasswordCommand.Execute(RepoPw.Password); RepoPw.Clear(); }
    private void SetSmtpPassword(object sender, RoutedEventArgs e) { Vm.Notifications.SetPasswordCommand.Execute(SmtpPw.Password); SmtpPw.Clear(); }
    private void SetSqlPassword(object sender, RoutedEventArgs e) { Vm.Jobs.Selected?.SetSqlPasswordCommand.Execute(SqlPw.Password); SqlPw.Clear(); }

    private void RestoreGridDoubleClick(object sender, MouseButtonEventArgs e) => Vm.Restore.OpenCommand.Execute(Vm.Restore.SelectedEntry);

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (Vm.IsDirty && MessageBox.Show(Gui.Core.Ui.T("l_unsaved_close"), "BackupForge", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) e.Cancel = true;
        base.OnClosing(e);
    }
}

public static class Lists
{
    public static string[] JobTypes => JobsViewModel.Types;
    public static string[] ScheduleKinds => JobsViewModel.ScheduleKindsList;
    public static string[] Modes => JobsViewModel.Modes;
    public static string[] Vss => JobsViewModel.VssList;
    public static string[] SqlModes => JobsViewModel.SqlModes;
    public static string[] Compression => JobsViewModel.CompressionList;
    public static string[] TargetTypes => TargetsViewModel.TargetTypesList;
    public static string[] Security => NotificationsViewModel.SecurityList;
    public static string[] Languages => SettingsViewModel.Languages;
}

public static class Converters
{
    public static readonly IValueConverter NotNullToVisible = new Fn(v => v is null || (v is string s && s.Length == 0) ? Visibility.Collapsed : Visibility.Visible);
    public static readonly IValueConverter InverseBoolToVis = new Fn(v => v is true ? Visibility.Collapsed : Visibility.Visible);

    private sealed class Fn(Func<object?, object> f) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => f(value);
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
