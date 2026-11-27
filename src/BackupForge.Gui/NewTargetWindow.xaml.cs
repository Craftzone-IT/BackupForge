using System.ComponentModel;
using System.Windows;
using BackupForge.Gui.Core.Wizard;

namespace BackupForge.Gui;

public partial class NewTargetWindow : Window
{
    public NewTargetWindow(NewTargetViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(NewTargetViewModel.Done) && vm.Done) DialogResult = true; };
    }

    private NewTargetViewModel Vm => (NewTargetViewModel)DataContext;

    // the password goes straight to secrets.set (the service tests with it), the box is cleared
    private async Task StorePasswordAsync()
    {
        if (Pw.Password.Length == 0) return;
        await Vm.SetPasswordAsync(Pw.Password);
        Pw.Clear();
    }

    private async void Test(object sender, RoutedEventArgs e)
    {
        try
        {
            await StorePasswordAsync();
            await Vm.TestAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { MessageBox.Show(ex.Message, "BackupForge"); }
    }

    private async void Ok(object sender, RoutedEventArgs e)
    {
        try
        {
            await StorePasswordAsync();
            Vm.Ok();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { MessageBox.Show(ex.Message, "BackupForge"); }
    }
}
