using System.Text.Json.Nodes;
using BackupForge.Core.Config;
using BackupForge.Gui.Core.ViewModels;
using BackupForge.Ipc;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BackupForge.Gui.Core.Wizard;

/// <summary>
/// "Új cél…" from the wizard (CR-002 B2./B3.): local folder or network share (+ user + password), connection test in the
/// service (LocalSystem). The password goes straight to secrets.set; a failed test needs an explicit confirmation to keep it.
/// </summary>
public sealed partial class NewTargetViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public NewTargetViewModel(MainViewModel main, string id)
    {
        _main = main;
        Id = id;
    }

    [ObservableProperty] public partial string Id { get; set; }
    [ObservableProperty] public partial bool IsSmb { get; set; } = true;
    [ObservableProperty] public partial string Path { get; set; } = "";
    [ObservableProperty] public partial string Username { get; set; } = "";
    [ObservableProperty] public partial string TestResult { get; set; } = "";
    [ObservableProperty] public partial bool? TestOk { get; set; }
    public bool IsLocal { get => !IsSmb; set => IsSmb = !value; }
    public TargetConfig? Result { get; private set; }
    /// <summary>The window closes when this turns true (OK) — false stays open.</summary>
    [ObservableProperty] public partial bool Done { get; set; }

    partial void OnIsSmbChanged(bool value) => OnPropertyChanged(nameof(IsLocal));

    public TargetConfig Build() => new()
    {
        Id = Id.Trim(),
        Type = IsSmb ? TargetTypes.Smb : TargetTypes.Local,
        Path = Path.Trim(),
        Username = IsSmb && !string.IsNullOrWhiteSpace(Username) ? Username.Trim() : null,
        PasswordRef = IsSmb && !string.IsNullOrWhiteSpace(Username) ? "target:" + Id.Trim() : null,
    };

    /// <summary>The password (from the PasswordBox) is stored as a secret first: the service tests with it.</summary>
    public async Task SetPasswordAsync(string password)
    {
        if (password.Length > 0) await _main.SetSecretAsync("target:" + Id.Trim(), password).ConfigureAwait(true);
    }

    [RelayCommand]
    public async Task TestAsync()
    {
        TestResult = Ui.T("testing");
        var r = await _main.TryCallQuietAsync(IpcOps.TargetTest, new JsonObject { ["target"] = Wizard.JobWizardViewModel.Node(Build()) }).ConfigureAwait(true);
        TestOk = r?["online"]?.GetValue<bool>() == true && r["writable"]?.GetValue<bool>() != false;
        TestResult = r is null ? Ui.T("test_no_service")
            : TestOk == true ? Ui.F("test_ok_writable", r["path"], r["free_gb"], r["account"])
            : Ui.F("test_failed", r["path"], r["error"], r["account"]);
    }

    [RelayCommand]
    public void Ok()
    {
        var t = Build();
        if (string.IsNullOrWhiteSpace(t.Path) || !Slug.IsValid(t.Id) || _main.EditConfig.FindTarget(t.Id) is not null)
        {
            TestResult = Ui.T("err_new_target");
            return;
        }
        if (TestOk != true && !_main.Dialogs.Confirm(Ui.T("confirm_untested_target"))) return;
        Result = t;
        Done = true;
    }
}
