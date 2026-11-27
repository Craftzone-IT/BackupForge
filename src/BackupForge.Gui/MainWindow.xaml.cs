using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using BackupForge.Gui.Core;
using BackupForge.Gui.Core.ViewModels;
using BackupForge.Gui.Core.Wizard;

namespace BackupForge.Gui;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private MainViewModel Vm => (MainViewModel)DataContext;

    // PasswordBox.Password is not bindable on purpose: the value goes straight to secrets.set and the box is cleared.
    private void SetTargetPassword(object sender, RoutedEventArgs e) { Vm.Targets.SelectedTarget?.SetPasswordCommand.Execute(TargetPw.Password); TargetPw.Clear(); }
    private void SetRepoPassword(object sender, RoutedEventArgs e) { Vm.Targets.SelectedRepo?.SetPasswordCommand.Execute(RepoPw.Password); RepoPw.Clear(); }
    private void SetSmtpPassword(object sender, RoutedEventArgs e) { Vm.Notifications.SetPasswordCommand.Execute(SmtpPw.Password); SmtpPw.Clear(); }
    private void EditJob(object sender, MouseButtonEventArgs e) => Vm.Jobs.EditCommand.Execute(null);

    private void RestoreGridDoubleClick(object sender, MouseButtonEventArgs e) => Vm.Restore.OpenCommand.Execute(Vm.Restore.SelectedEntry);

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (Vm.IsDirty && MessageBox.Show(Gui.Core.Ui.T("l_unsaved_close"), "BackupForge", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) e.Cancel = true;
        base.OnClosing(e);
    }
}

public static class Lists
{
    private static Choice[] C(params (string Id, string Key)[] items) => [.. items.Select(i => new Choice(i.Id, Ui.T(i.Key)))];
    public static IReadOnlyList<Choice> Vss => JobWizardViewModel.VssChoices;
    public static Choice[] Compression => C(("auto", "c_auto"), ("on", "c_on"), ("off", "c_off"));
    public static Choice[] Days => C(("Mon", "d_mon"), ("Tue", "d_tue"), ("Wed", "d_wed"), ("Thu", "d_thu"), ("Fri", "d_fri"), ("Sat", "d_sat"), ("Sun", "d_sun"));
    public static Choice[] MonthDays => [.. Enumerable.Range(1, 28).Select(d => new Choice(d.ToString(CultureInfo.InvariantCulture), d + ".")), new Choice("last", Ui.T("w_last_day"))];
    public static string[] Security => NotificationsViewModel.SecurityList;
    public static string[] Languages => SettingsViewModel.Languages;
}

public static class Converters
{
    public static readonly IValueConverter NotNullToVisible = new Fn(v => v is null || (v is string s && s.Length == 0) ? Visibility.Collapsed : Visibility.Visible);
    public static readonly IValueConverter InverseBoolToVis = new Fn(v => v is true ? Visibility.Collapsed : Visibility.Visible);
    /// <summary>Current wizard step == ConverterParameter → Visible.</summary>
    public static readonly IValueConverter StepIs = new Fn((v, p) => v?.ToString() == p?.ToString() ? Visibility.Visible : Visibility.Collapsed);
    public static readonly IValueConverter EqualsToVis = new Fn((v, p) => v?.ToString() == p?.ToString() ? Visibility.Visible : Visibility.Collapsed);
    /// <summary>A radio button for one value of a string property (two-way).</summary>
    public static readonly IValueConverter EqualsParam = new Fn((v, p) => v?.ToString() == p?.ToString(), (v, p) => v is true ? p : Binding.DoNothing);
    public static readonly IValueConverter Not = new Fn((v, _) => v is not true, (v, _) => v is not true);

    private sealed class Fn(Func<object?, object?, object?> to, Func<object?, object?, object?>? back = null) : IValueConverter
    {
        public Fn(Func<object?, object> to) : this((v, _) => to(v)) { }
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => to(value, parameter);
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => back is null ? Binding.DoNothing : back(value, parameter);
    }
}
