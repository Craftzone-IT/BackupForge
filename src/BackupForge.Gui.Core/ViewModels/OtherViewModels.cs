using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;
using BackupForge.Core.Config;
using BackupForge.Engine.Sql;
using BackupForge.Ipc;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BackupForge.Gui.Core.ViewModels;

public sealed record SqlDbRow(string Instance, string Name, string RecoveryModel, string Size, string LastFullAny, bool System, string? Warning);

public sealed partial class SqlViewModel(MainViewModel main) : ObservableObject
{
    public ObservableCollection<SqlDbRow> Databases { get; } = [];
    public ObservableCollection<string> Problems { get; } = [];
    [ObservableProperty] public partial string Script { get; set; } = "";
    [ObservableProperty] public partial string? GrantJobId { get; set; }

    public IEnumerable<string> SqlJobIds => main.EditConfig.Jobs.Where(j => j.Type == JobTypes.Sql).Select(j => j.Id);

    [RelayCommand]
    private async Task DiscoverAsync()
    {
        var r = await main.TryCallAsync(IpcOps.SqlDiscover).ConfigureAwait(true);
        if (r is null) return;
        Databases.Clear();
        Problems.Clear();
        foreach (var inst in r.AsArray())
        {
            if (inst!["error"] is { } err) Problems.Add($"{inst["instance"]}: {err}");
            foreach (var d in inst["databases"]?.AsArray() ?? [])
            {
                var rm = d!["recovery_model"]?.ToString() ?? "";
                var size = J.L(d["size_bytes"]) is { } b ? BackupForge.Core.Resources.SizeText.Format(b, Ui.Culture) : "?";
                var sys = d["system"]?.GetValue<bool>() == true;
                Databases.Add(new SqlDbRow(inst["instance"]!.ToString(), d["name"]!.ToString(), rm, size, OverviewViewModel.Time(d["last_full_any"]), sys,
                    !sys && rm is "FULL" or "BULK_LOGGED" ? Ui.T("full_recovery_hint") : null));
            }
        }
        OnPropertyChanged(nameof(SqlJobIds));
    }

    /// <summary>"Jogosultságok beállítása": the GUI (elevated, as the logged-on administrator) builds and shows the T-SQL, then runs it.</summary>
    [RelayCommand]
    private async Task GrantAsync()
    {
        var job = main.EditConfig.FindJob(GrantJobId ?? SqlJobIds.FirstOrDefault() ?? "");
        if (job?.Sql is null) return;
        try
        {
            await using var s = await new SqlClientSessionFactory().OpenAsync(job.Sql.Instance, null, null, CancellationToken.None).ConfigureAwait(true);
            var dbs = SqlPlanning.Select(job.Sql, await s.ListDatabasesAsync(CancellationToken.None).ConfigureAwait(true)).Select(d => d.Name).ToList();
            Script = SqlPlanning.GrantScript(dbs, !string.IsNullOrEmpty(job.Sql.Checkdb));
            if (!main.Dialogs.Confirm(Ui.T("confirm_grant") + Environment.NewLine + Environment.NewLine + Script)) return;
            await s.ExecuteAsync(Script, CancellationToken.None).ConfigureAwait(true);
            main.Dialogs.Info(Ui.F("grant_done", dbs.Count));
        }
        catch (SqlCommandException ex)
        {
            main.Dialogs.Error(ex.Message);
        }
    }
}

public sealed partial class HistoryViewModel(MainViewModel main) : ObservableObject
{
    public ObservableCollection<JsonNode> Runs { get; } = [];
    [ObservableProperty] public partial string? JobFilter { get; set; }
    [ObservableProperty] public partial JsonNode? Selected { get; set; }
    [ObservableProperty] public partial string Details { get; set; } = "";
    [ObservableProperty] public partial string Log { get; set; } = "";

    public IEnumerable<string> JobIds => main.EditConfig.Jobs.Select(j => j.Id);

    [RelayCommand]
    public async Task LoadAsync()
    {
        var args = new JsonObject { ["last"] = 200 };
        if (!string.IsNullOrEmpty(JobFilter)) args["job_id"] = JobFilter;
        var r = await main.TryCallAsync(IpcOps.HistoryList, args).ConfigureAwait(true);
        Runs.Clear();
        foreach (var x in r?.AsArray() ?? []) Runs.Add(x!);
    }

    async partial void OnSelectedChanged(JsonNode? value)
    {
        try
        {
            if (J.L(value?["run_id"]) is not { } id) return;
            var d = await main.TryCallAsync(IpcOps.HistoryGet, new JsonObject { ["run_id"] = id }).ConfigureAwait(true);
            if (d is null) return;
            var items = d["items"]?.AsArray().Select(i => $"{i!["level"]}: {i["path"]} {i["message"]}") ?? [];
            Details = $"{d["job_id"]} · {d["kind"]} · {d["trigger"]} · {d["result"]}{Environment.NewLine}{d["start"]} → {d["end"]}{Environment.NewLine}{d["summary"]}{Environment.NewLine}{d["log_path"]}{Environment.NewLine}{string.Join(Environment.NewLine, items)}";
            var log = await main.TryCallAsync(IpcOps.LogTail, new JsonObject { ["run_id"] = id, ["lines"] = 500 }).ConfigureAwait(true);
            Log = string.Join(Environment.NewLine, log?["lines"]?.AsArray().Select(l => l?.ToString()) ?? []);
        
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            main.Dialogs.Error(ex.Message);
        }
    }
}

public sealed record BrowseEntry(string Name, bool IsDirectory, string Size, string Modified);

/// <summary>SPEC 13.: point → browse → target (a new folder by default) → restore.</summary>
public sealed partial class RestoreViewModel(MainViewModel main) : ObservableObject
{
    public ObservableCollection<JsonNode> Points { get; } = [];
    public ObservableCollection<BrowseEntry> Entries { get; } = [];
    [ObservableProperty] public partial string? JobId { get; set; }
    [ObservableProperty] public partial JsonNode? Point { get; set; }
    [ObservableProperty] public partial string CurrentPath { get; set; } = "";
    [ObservableProperty] public partial BrowseEntry? SelectedEntry { get; set; }
    [ObservableProperty] public partial string TargetFolder { get; set; } = "";
    [ObservableProperty] public partial bool ToOriginal { get; set; }
    [ObservableProperty] public partial string Result { get; set; } = "";

    public IEnumerable<string> JobIds => main.EditConfig.Jobs.Where(j => j.Type is JobTypes.Files or JobTypes.Restic).Select(j => j.Id);
    private bool IsRestic => main.EditConfig.FindJob(JobId ?? "")?.Type == JobTypes.Restic;
    private char Sep => IsRestic ? '/' : '\\';

    async partial void OnJobIdChanged(string? value)
    {
        try
        {
            Points.Clear();
            Entries.Clear();
            CurrentPath = "";
            if (value is null) return;
            foreach (var p in (await main.TryCallAsync(IpcOps.RestoreVersions, new JsonObject { ["job_id"] = value }).ConfigureAwait(true))?.AsArray() ?? []) Points.Add(p!);
            Point = Points.FirstOrDefault();
        
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            main.Dialogs.Error(ex.Message);
        }
    }

    async partial void OnPointChanged(JsonNode? value)
    {
        try
        {
            CurrentPath = "";
            await BrowseAsync().ConfigureAwait(true);
        
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            main.Dialogs.Error(ex.Message);
        }
    }

    [RelayCommand]
    public async Task BrowseAsync()
    {
        Entries.Clear();
        if (JobId is null || Point is null) return;
        var r = await main.TryCallAsync(IpcOps.RestoreBrowse, new JsonObject { ["job_id"] = JobId, ["point"] = Point["point"]?.ToString(), ["path"] = CurrentPath }).ConfigureAwait(true);
        foreach (var e in r?.AsArray() ?? [])
            Entries.Add(new BrowseEntry(e!["name"]!.ToString(), e["dir"]?.GetValue<bool>() == true,
                e["dir"]?.GetValue<bool>() == true ? "" : BackupForge.Core.Resources.SizeText.Format(J.L(e["size"]) ?? 0, Ui.Culture), OverviewViewModel.Time(e["mtime"])));
    }

    [RelayCommand]
    private async Task OpenAsync(BrowseEntry? entry)
    {
        entry ??= SelectedEntry;
        if (entry is not { IsDirectory: true }) return;
        CurrentPath = CurrentPath.Length == 0 ? entry.Name : CurrentPath + Sep + entry.Name;
        await BrowseAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task UpAsync()
    {
        var i = CurrentPath.LastIndexOf(Sep);
        CurrentPath = i < 0 ? "" : CurrentPath[..i];
        await BrowseAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void PickTarget()
    {
        if (main.Dialogs.PickFolder(TargetFolder) is { } f) TargetFolder = f;
    }

    [RelayCommand]
    private async Task RestoreAsync()
    {
        if (JobId is null || Point is null) return;
        var path = SelectedEntry is { } e ? (CurrentPath.Length == 0 ? e.Name : CurrentPath + Sep + e.Name) : CurrentPath;
        if (IsRestic && path.Length > 0 && !path.StartsWith('/')) path = "/" + path;
        if (ToOriginal && !main.Dialogs.Confirm(Ui.T("confirm_restore_original"))) return;
        if (!ToOriginal && string.IsNullOrWhiteSpace(TargetFolder)) { main.Dialogs.Error(Ui.T("restore_target_missing")); return; }
        Result = Ui.T("restore_running");
        var r = await main.TryCallAsync(IpcOps.RestoreRun, new JsonObject
        {
            ["job_id"] = JobId, ["point"] = Point["point"]?.ToString(), ["path"] = path,
            ["to"] = ToOriginal ? null : TargetFolder, ["to_original"] = ToOriginal, ["confirm"] = ToOriginal, ["wait"] = true,
        }).ConfigureAwait(true);
        Result = r is null ? "" : $"{r["result"]}: {r["summary"]}";
    }
}

public sealed partial class NotificationsViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public NotificationsViewModel(MainViewModel main)
    {
        _main = main;
        main.ConfigReloaded += Load;
    }

    [ObservableProperty] public partial bool Enabled { get; set; }
    [ObservableProperty] public partial string Host { get; set; } = "";
    [ObservableProperty] public partial string Port { get; set; } = "587";
    [ObservableProperty] public partial string Security { get; set; } = SmtpSecurityModes.StartTls;
    [ObservableProperty] public partial string Username { get; set; } = "";
    [ObservableProperty] public partial string From { get; set; } = "";
    [ObservableProperty] public partial string To { get; set; } = "";
    [ObservableProperty] public partial bool OnFailure { get; set; }
    [ObservableProperty] public partial bool OnWarning { get; set; }
    [ObservableProperty] public partial bool OnRecovery { get; set; }
    [ObservableProperty] public partial string WeeklyReport { get; set; } = "";
    [ObservableProperty] public partial string HeartbeatUrl { get; set; } = "";
    [ObservableProperty] public partial bool EventLog { get; set; }
    public static string[] SecurityList => SmtpSecurityModes.All;
    public bool PasswordIsSet => _main.SetSecrets.Contains("smtp");

    public void Load()
    {
        var n = _main.EditConfig.Notifications;
        var e = n.Email;
        Enabled = e.Enabled; Host = e.SmtpHost; Port = e.SmtpPort.ToString(CultureInfo.InvariantCulture); Security = e.Security;
        Username = e.Username ?? ""; From = e.From; To = string.Join(", ", e.To);
        OnFailure = e.OnFailure; OnWarning = e.OnWarning; OnRecovery = e.OnRecovery; WeeklyReport = e.WeeklyReport ?? "";
        HeartbeatUrl = n.HeartbeatUrl ?? ""; EventLog = n.EventLog;
        OnPropertyChanged(nameof(PasswordIsSet));
    }

    public void ApplyTo(BackupConfig cfg)
    {
        var e = cfg.Notifications.Email;
        e.Enabled = Enabled; e.SmtpHost = Host.Trim(); e.SmtpPort = int.TryParse(Port, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 0; e.Security = Security;
        e.Username = string.IsNullOrWhiteSpace(Username) ? null : Username.Trim();
        e.PasswordRef = e.Username is null ? null : "smtp";
        e.From = From.Trim();
        e.To = [.. To.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        e.OnFailure = OnFailure; e.OnWarning = OnWarning; e.OnRecovery = OnRecovery;
        e.WeeklyReport = string.IsNullOrWhiteSpace(WeeklyReport) ? null : WeeklyReport.Trim();
        cfg.Notifications.HeartbeatUrl = string.IsNullOrWhiteSpace(HeartbeatUrl) ? null : HeartbeatUrl.Trim();
        cfg.Notifications.EventLog = EventLog;
    }

    [RelayCommand]
    private async Task SetPasswordAsync(string? password)
    {
        if (!string.IsNullOrEmpty(password) && await _main.SetSecretAsync("smtp", password).ConfigureAwait(true)) { OnPropertyChanged(nameof(PasswordIsSet)); _main.Dialogs.Info(Ui.T("password_saved")); }
    }

    [RelayCommand]
    private async Task TestAsync()
    {
        if (_main.IsDirty && !await _main.SaveAsync().ConfigureAwait(true)) return;
        if (await _main.TryCallAsync(IpcOps.NotifyTest).ConfigureAwait(true) is not null) _main.Dialogs.Info(Ui.T("test_mail_sent"));
    }

    [RelayCommand]
    private async Task PreviewReportAsync()
    {
        var r = await _main.TryCallAsync(IpcOps.ReportPreview).ConfigureAwait(true);
        if (r is not null) _main.Dialogs.ShowText(r["subject"]?.ToString() ?? "", r["text"]?.ToString() ?? "", printable: false);
    }
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        main.ConfigReloaded += Load;
    }

    [ObservableProperty] public partial string InstanceName { get; set; } = "";
    [ObservableProperty] public partial string Language { get; set; } = "hu";
    [ObservableProperty] public partial string MaxParallel { get; set; } = "2";
    [ObservableProperty] public partial string HistoryDays { get; set; } = "400";
    [ObservableProperty] public partial string LogDays { get; set; } = "30";
    public static string[] Languages => ["hu", "en"];

    public void Load()
    {
        var c = _main.EditConfig;
        InstanceName = c.InstanceName; Language = c.Language;
        MaxParallel = c.General.MaxParallelJobs.ToString(CultureInfo.InvariantCulture);
        HistoryDays = c.General.HistoryRetentionDays.ToString(CultureInfo.InvariantCulture);
        LogDays = c.General.LogRetentionDays.ToString(CultureInfo.InvariantCulture);
    }

    private static int I(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : -1;

    public void ApplyTo(BackupConfig cfg)
    {
        cfg.InstanceName = InstanceName.Trim();
        cfg.Language = Language;
        cfg.General.MaxParallelJobs = I(MaxParallel);
        cfg.General.HistoryRetentionDays = I(HistoryDays);
        cfg.General.LogRetentionDays = I(LogDays);
    }

    /// <summary>Export without secrets (only their references are in config.json anyway).</summary>
    [RelayCommand]
    private void Export()
    {
        if (_main.Dialogs.SaveFile("backupforge-config.json", "JSON|*.json") is not { } path) return;
        File.WriteAllText(path, ConfigJson.Serialize(_main.EditConfig));
        _main.Dialogs.Info(Ui.F("exported", path));
    }

    /// <summary>Import: validated by the service on save; missing secrets are listed so they can be entered.</summary>
    [RelayCommand]
    private async Task ImportAsync()
    {
        if (_main.Dialogs.OpenFile("JSON|*.json") is not { } path) return;
        BackupConfig cfg;
        try { cfg = ConfigJson.Parse(File.ReadAllText(path)); }
        catch (ConfigFormatException ex) { _main.Dialogs.Error(ex.Message); return; }
        var r = await _main.TryCallAsync(IpcOps.ConfigSave, new JsonObject { ["config"] = ConfigJson.Serialize(cfg) }).ConfigureAwait(true);
        if (r is null) return;
        await _main.RefreshAsync().ConfigureAwait(true);
        var missing = MissingSecrets(cfg, _main.SetSecrets);
        _main.Dialogs.Info(missing.Count == 0 ? Ui.T("imported") : Ui.F("imported_missing", string.Join(", ", missing)));
    }

    public static List<string> MissingSecrets(BackupConfig cfg, ISet<string> set)
    {
        var refs = cfg.Targets.Select(t => t.PasswordRef)
            .Concat(cfg.ResticRepos.Select(r => (string?)r.PasswordRef))
            .Concat(cfg.ResticRepos.SelectMany(r => r.EnvRefs.Values).Select(v => (string?)v))
            .Concat(cfg.Jobs.Select(j => j.Sql?.LoginRef))
            .Append(cfg.Notifications.Email.PasswordRef);
        return [.. refs.Where(r => !string.IsNullOrEmpty(r) && !set.Contains(r!)).Select(r => r!).Distinct()];
    }
}
