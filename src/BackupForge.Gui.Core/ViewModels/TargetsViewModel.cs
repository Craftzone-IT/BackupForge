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
        _ = CheckReposAsync();
    }

    /// <summary>"nincs inicializálva" badge (G4): a saved repository restic does not find at its location.</summary>
    public async Task CheckReposAsync()
    {
        foreach (var r in Repos.ToList())
        {
            if (_main.EditConfig.FindRepo(r.Id) is null) continue;
            try
            {
                await _main.Client.CallAsync(IpcOps.RepoSnapshots, new JsonObject { ["repo_id"] = r.Id }).ConfigureAwait(true);
                r.NotInitialized = false;
            }
            catch (IpcException ex) { r.NotInitialized = IsNotInitialized(ex.Message); }
            catch (IpcUnavailableException) { return; }
        }
    }

    /// <summary>restic's / the service's "the repository does not exist" message (en / hu).</summary>
    public static bool IsNotInitialized(string message) =>
        message.Contains("does not exist", StringComparison.OrdinalIgnoreCase) || message.Contains("nem létezik", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("not initialized", StringComparison.OrdinalIgnoreCase) || message.Contains("unable to open config file", StringComparison.OrdinalIgnoreCase);

    public void ApplyTo(BackupConfig cfg)
    {
        cfg.Targets = [.. Targets.Select(t => t.ToTarget())];
        cfg.ResticRepos = [.. Repos.Select(r => r.ToRepo())];
    }

    [RelayCommand]
    private void AddTarget()
    {
        // the example path is a watermark in the box (G3), not a value
        var vm = new TargetEditViewModel(new TargetConfig { Id = Wizard.Slug.Unique("nas", Targets.Select(t => t.Id)), Type = TargetTypes.Smb, Path = "" }, _main);
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
        TestResult = r["online"]?.GetValue<bool>() != true ? Ui.F("test_failed", r["path"], r["error"], r["account"])
            : r["writable"]?.GetValue<bool>() == false ? Ui.F("test_not_writable", r["path"], r["error"], r["account"])
            : Ui.F("test_ok_writable", r["path"], r["free_gb"], r["account"]);
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
    private async Task CreateRepoAsync() => await CreateSelectedRepoAsync().ConfigureAwait(true);

    private async Task<bool> CreateSelectedRepoAsync()
    {
        if (SelectedRepo is not { } repo || string.IsNullOrWhiteSpace(repo.Location)) { _main.Dialogs.Error(Ui.T("repo_location_missing")); return false; }
        if (!_main.Dialogs.Confirm(Ui.F("confirm_repo_init", repo.Location))) return false;
        var password = GeneratePassword();
        if (!await _main.SetSecretAsync(repo.PasswordRef, password).ConfigureAwait(true)) return false;
        _main.Dialogs.ShowText(Ui.T("recovery_sheet_title"), RecoverySheet(repo.Location, password, DateTime.Now, Environment.MachineName), printable: true);
        if (!await _main.SaveAsync().ConfigureAwait(true)) return false;
        var r = await _main.TryCallAsync(IpcOps.RepoInit, new JsonObject { ["repo_id"] = repo.Id, ["confirm"] = true }).ConfigureAwait(true);
        if (r is null) return false;
        _main.Dialogs.Info(Ui.T("repo_created"));
        if (Repos.FirstOrDefault(x => x.Id == repo.Id) is { } again) again.NotInitialized = false;
        return true;
    }

    /// <summary>
    /// From the job wizard (G4): "Új tároló…" (asks for the id and the location) or "Tároló létrehozása most…" for an
    /// existing, not yet initialized one — then the same repository wizard. Returns the repository id, or null.
    /// </summary>
    public async Task<string?> CreateRepoInteractiveAsync(string? existingId)
    {
        if (existingId is not null)
        {
            SelectedRepo = Repos.FirstOrDefault(r => r.Id == existingId);
            return SelectedRepo is not null && await CreateSelectedRepoAsync().ConfigureAwait(true) ? existingId : null;
        }
        var ask = new NewRepoViewModel(Wizard.Slug.Unique("offsite", Repos.Select(r => r.Id)));
        if (!_main.Dialogs.ShowDialog(ask) || string.IsNullOrWhiteSpace(ask.Location) || !Wizard.Slug.IsValid(ask.Id.Trim())) return null;
        var id = ask.Id.Trim();
        var vm = new RepoEditViewModel(new ResticRepoConfig { Id = id, Location = ask.Location.Trim(), PasswordRef = "restic:" + id }, _main);
        Repos.Add(vm);
        SelectedRepo = vm;
        if (await CreateSelectedRepoAsync().ConfigureAwait(true)) return id;
        return Repos.Any(r => r.Id == id) && _main.EditConfig.FindRepo(id) is not null ? id : null;
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
    /// <summary>The type as two radio buttons (G1): the user name and the password only for a network share.</summary>
    public bool IsSmb { get => Type == TargetTypes.Smb; set { if (value) Type = TargetTypes.Smb; } }
    public bool IsLocal { get => Type == TargetTypes.Local; set { if (value) Type = TargetTypes.Local; } }

    partial void OnTypeChanged(string value)
    {
        OnPropertyChanged(nameof(IsSmb));
        OnPropertyChanged(nameof(IsLocal));
    }
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
    /// <summary>restic found no repository at the location (badge + "Létrehozás…", G4).</summary>
    [ObservableProperty] public partial bool NotInitialized { get; set; }

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

/// <summary>"Új tároló…" from the wizard: the id and the location (local / UNC path or an sftp: / s3: / rest: URL).</summary>
public sealed partial class NewRepoViewModel(string id) : ObservableObject
{
    [ObservableProperty] public partial string Id { get; set; } = id;
    [ObservableProperty] public partial string Location { get; set; } = "";
}
