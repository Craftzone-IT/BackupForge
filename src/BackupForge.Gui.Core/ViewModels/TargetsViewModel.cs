using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using BackupForge.Core.Config;
using BackupForge.Ipc;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BackupForge.Gui.Core.ViewModels;

public sealed partial class TargetsViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public TargetsViewModel(MainViewModel main)
    {
        _main = main;
        main.ConfigReloaded += Load;
    }

    public ObservableCollection<TargetEditViewModel> Targets { get; } = [];
    public ObservableCollection<RepoEditViewModel> Repos { get; } = [];
    [ObservableProperty] public partial TargetEditViewModel? SelectedTarget { get; set; }
    [ObservableProperty] public partial RepoEditViewModel? SelectedRepo { get; set; }
    [ObservableProperty] public partial string TestResult { get; set; } = "";
    public static string[] TargetTypesList => TargetTypes.All;

    public void Load()
    {
        Targets.Clear();
        foreach (var t in _main.EditConfig.Targets) Targets.Add(new TargetEditViewModel(t, _main));
        Repos.Clear();
        foreach (var r in _main.EditConfig.ResticRepos) Repos.Add(new RepoEditViewModel(r, _main));
        SelectedTarget = Targets.FirstOrDefault();
        SelectedRepo = Repos.FirstOrDefault();
    }

    public void ApplyTo(BackupConfig cfg)
    {
        cfg.Targets = [.. Targets.Select(t => t.ToTarget())];
        cfg.ResticRepos = [.. Repos.Select(r => r.ToRepo())];
    }

    [RelayCommand]
    private void AddTarget()
    {
        var vm = new TargetEditViewModel(new TargetConfig { Id = "target" + (Targets.Count + 1), Type = TargetTypes.Smb, Path = @"\\server\share\" + Environment.MachineName }, _main);
        Targets.Add(vm);
        SelectedTarget = vm;
        _main.MarkDirty();
    }

    [RelayCommand]
    private void RemoveTarget()
    {
        if (SelectedTarget is null || !_main.Dialogs.Confirm(Ui.F("confirm_remove_target", SelectedTarget.Id))) return;
        Targets.Remove(SelectedTarget);
        SelectedTarget = Targets.FirstOrDefault();
        _main.MarkDirty();
    }

    /// <summary>"Kapcsolat tesztelése" — runs in the service, i.e. as LocalSystem (SPEC 11.2.3). Uses the saved configuration.</summary>
    [RelayCommand]
    private async Task TestTargetAsync()
    {
        if (SelectedTarget is null) return;
        var r = await _main.TryCallAsync(IpcOps.TargetTest, new JsonObject { ["target_id"] = SelectedTarget.Id }).ConfigureAwait(true);
        if (r is null) return;
        TestResult = r["online"]?.GetValue<bool>() == true
            ? Ui.F("test_ok", r["path"], r["free_gb"], r["account"])
            : Ui.F("test_failed", r["path"], r["error"], r["account"]);
    }

    [RelayCommand]
    private void AddRepo()
    {
        var id = "repo" + (Repos.Count + 1);
        var vm = new RepoEditViewModel(new ResticRepoConfig { Id = id, Location = "", PasswordRef = "restic:" + id }, _main);
        Repos.Add(vm);
        SelectedRepo = vm;
        _main.MarkDirty();
    }

    [RelayCommand]
    private void RemoveRepo()
    {
        if (SelectedRepo is null || !_main.Dialogs.Confirm(Ui.F("confirm_remove_repo", SelectedRepo.Id))) return;
        Repos.Remove(SelectedRepo);
        SelectedRepo = Repos.FirstOrDefault();
        _main.MarkDirty();
    }

    /// <summary>
    /// New repository wizard (SPEC 7.3): generates a 32-character password, stores it, shows it once with the recovery sheet,
    /// saves the configuration and — only after an explicit confirmation — runs restic init.
    /// </summary>
    [RelayCommand]
    private async Task CreateRepoAsync()
    {
        if (SelectedRepo is not { } repo || string.IsNullOrWhiteSpace(repo.Location)) { _main.Dialogs.Error(Ui.T("repo_location_missing")); return; }
        if (!_main.Dialogs.Confirm(Ui.F("confirm_repo_init", repo.Location))) return;
        var password = GeneratePassword();
        if (!await _main.SetSecretAsync(repo.PasswordRef, password).ConfigureAwait(true)) return;
        _main.Dialogs.ShowText(Ui.T("recovery_sheet_title"), RecoverySheet(repo.Location, password, DateTime.Now, Environment.MachineName), printable: true);
        if (!await _main.SaveAsync().ConfigureAwait(true)) return;
        var r = await _main.TryCallAsync(IpcOps.RepoInit, new JsonObject { ["repo_id"] = repo.Id, ["confirm"] = true }).ConfigureAwait(true);
        if (r is not null) _main.Dialogs.Info(Ui.T("repo_created"));
    }

    public const string PasswordAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    public static string GeneratePassword(int length = 32) =>
        new(Enumerable.Range(0, length).Select(_ => PasswordAlphabet[RandomNumberGenerator.GetInt32(PasswordAlphabet.Length)]).ToArray());

    public static string RecoverySheet(string location, string password, DateTime when, string machine) =>
        Ui.F("recovery_sheet", location, password, when.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), machine);
}

public sealed partial class TargetEditViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public TargetEditViewModel(TargetConfig t, MainViewModel main)
    {
        _main = main;
        Id = t.Id;
        Type = t.Type;
        Path = t.Path;
        Username = t.Username ?? "";
        MinFreeGb = t.MinFreeGb?.ToString(CultureInfo.InvariantCulture) ?? "";
        PasswordRef = t.PasswordRef ?? "target:" + t.Id;
        PropertyChanged += (_, _) => main.MarkDirty();
    }

    [ObservableProperty] public partial string Id { get; set; }
    [ObservableProperty] public partial string Type { get; set; }
    [ObservableProperty] public partial string Path { get; set; }
    [ObservableProperty] public partial string Username { get; set; }
    [ObservableProperty] public partial string MinFreeGb { get; set; }
    public string PasswordRef { get; private set; }
    public bool PasswordIsSet => _main.SetSecrets.Contains(PasswordRef);

    public TargetConfig ToTarget() => new()
    {
        Id = Id.Trim(),
        Type = Type,
        Path = Path.Trim(),
        Username = string.IsNullOrWhiteSpace(Username) ? null : Username.Trim(),
        PasswordRef = string.IsNullOrWhiteSpace(Username) ? null : PasswordRef,
        MinFreeGb = double.TryParse(MinFreeGb, NumberStyles.Float, CultureInfo.InvariantCulture, out var g) ? g : null,
    };

    [RelayCommand]
    private async Task SetPasswordAsync(string? password)
    {
        if (string.IsNullOrEmpty(password)) return;
        if (await _main.SetSecretAsync(PasswordRef, password).ConfigureAwait(true)) { OnPropertyChanged(nameof(PasswordIsSet)); _main.Dialogs.Info(Ui.T("password_saved")); }
    }
}

public sealed partial class RepoEditViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public RepoEditViewModel(ResticRepoConfig r, MainViewModel main)
    {
        _main = main;
        Id = r.Id;
        Location = r.Location;
        PasswordRef = r.PasswordRef;
        AppendOnly = r.AppendOnlyServer;
        KeepLast = N(r.Retention.KeepLast);
        KeepDaily = N(r.Retention.KeepDaily);
        KeepWeekly = N(r.Retention.KeepWeekly);
        KeepMonthly = N(r.Retention.KeepMonthly);
        KeepYearly = N(r.Retention.KeepYearly);
        ForgetPrune = r.Maintenance.ForgetPrune ?? "";
        Check = r.Maintenance.Check ?? "";
        Subset = r.Maintenance.CheckReadDataSubset;
        EnvRefs = r.EnvRefs;
        PropertyChanged += (_, _) => main.MarkDirty();
    }

    private static string N(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";
    private static int? I(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    private Dictionary<string, string> EnvRefs { get; }

    [ObservableProperty] public partial string Id { get; set; }
    [ObservableProperty] public partial string Location { get; set; }
    [ObservableProperty] public partial bool AppendOnly { get; set; }
    [ObservableProperty] public partial string KeepLast { get; set; }
    [ObservableProperty] public partial string KeepDaily { get; set; }
    [ObservableProperty] public partial string KeepWeekly { get; set; }
    [ObservableProperty] public partial string KeepMonthly { get; set; }
    [ObservableProperty] public partial string KeepYearly { get; set; }
    [ObservableProperty] public partial string ForgetPrune { get; set; }
    [ObservableProperty] public partial string Check { get; set; }
    [ObservableProperty] public partial string Subset { get; set; }
    public string PasswordRef { get; }
    public bool PasswordIsSet => _main.SetSecrets.Contains(PasswordRef);
    public string AppendOnlyHint => Ui.T("append_only_hint");

    public ResticRepoConfig ToRepo() => new()
    {
        Id = Id.Trim(),
        Location = Location.Trim(),
        PasswordRef = PasswordRef,
        EnvRefs = EnvRefs,
        AppendOnlyServer = AppendOnly,
        Retention = new ResticRetention { KeepLast = I(KeepLast), KeepDaily = I(KeepDaily), KeepWeekly = I(KeepWeekly), KeepMonthly = I(KeepMonthly), KeepYearly = I(KeepYearly) },
        Maintenance = new ResticMaintenance { ForgetPrune = string.IsNullOrWhiteSpace(ForgetPrune) ? null : ForgetPrune, Check = string.IsNullOrWhiteSpace(Check) ? null : Check, CheckReadDataSubset = Subset },
    };

    /// <summary>An existing repository: enter its password (it is only stored, never shown again).</summary>
    [RelayCommand]
    private async Task SetPasswordAsync(string? password)
    {
        if (string.IsNullOrEmpty(password)) return;
        if (await _main.SetSecretAsync(PasswordRef, password).ConfigureAwait(true)) { OnPropertyChanged(nameof(PasswordIsSet)); _main.Dialogs.Info(Ui.T("password_saved")); }
    }
}
