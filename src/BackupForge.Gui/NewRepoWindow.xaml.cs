using System.Windows;
using BackupForge.Gui.Core.ViewModels;

namespace BackupForge.Gui;

public partial class NewRepoWindow : Window
{
    public NewRepoWindow(NewRepoViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void Ok(object sender, RoutedEventArgs e) => DialogResult = true;
}
