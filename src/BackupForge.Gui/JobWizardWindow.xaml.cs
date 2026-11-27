using System.ComponentModel;
using System.Windows;
using BackupForge.Gui.Core.Wizard;

namespace BackupForge.Gui;

/// <summary>The job wizard (CR-002 B2.) — all logic is in <see cref="JobWizardViewModel"/>; this only binds.</summary>
public partial class JobWizardWindow : Window
{
    public JobWizardWindow(JobWizardViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.PropertyChanged += OnVmChanged;
    }

    private JobWizardViewModel Vm => (JobWizardViewModel)DataContext;

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(JobWizardViewModel.Completed) && Vm.Completed) DialogResult = true;
    }

    // the SQL login password is stored as a secret on Finish, never bound
    private void SqlPasswordChanged(object sender, RoutedEventArgs e) => Vm.SqlPassword = SqlPw.Password;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (DialogResult != true && !Vm.Completed &&
            MessageBox.Show(Gui.Core.Ui.T("w_confirm_cancel"), "BackupForge", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            e.Cancel = true;
        base.OnClosing(e);
    }
}
