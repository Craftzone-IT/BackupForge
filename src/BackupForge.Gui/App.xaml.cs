using System.Windows;
using BackupForge.Gui.Core;
using BackupForge.Gui.Core.ViewModels;

namespace BackupForge.Gui;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var client = new PipeServiceClient();
        var vm = new MainViewModel(client, new WpfDialogs());
        var window = new MainWindow { DataContext = vm };
        MainWindow = window;
        window.Show();
        await vm.RefreshAsync();
    }
}
