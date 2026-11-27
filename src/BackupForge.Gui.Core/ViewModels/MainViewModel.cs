using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using BackupForge.Core.Config;
using BackupForge.Ipc;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BackupForge.Gui.Core.ViewModels;

/// <summary>Root of the configurator (SPEC 11.2). Pages edit <see cref="EditConfig"/>; Save sends it to the service (which validates).</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IServiceClient _client;

    public MainViewModel(IServiceClient client, IUserDialogs dialogs)
    {
        _client = client;
        Dialogs = dialogs;
        Overview = new OverviewViewModel(this);
        Jobs = new JobsViewModel(this);
        Targets = new TargetsViewModel(this);
        Sql = new SqlViewModel(this);
        History = new HistoryViewModel(this);
        Restore = new RestoreViewModel(this);
        Notifications = new NotificationsViewModel(this);
        Settings = new SettingsViewModel(this);
        client.EventReceived += OnEvent;
    }

    public IServiceClient Client => _client;
    public IUserDialogs Dialogs { get; }
    public OverviewViewModel Overview { get; }
    public JobsViewModel Jobs { get; }
    public TargetsViewModel Targets { get; }
    public SqlViewModel Sql { get; }
    public HistoryViewModel History { get; }
    public RestoreViewModel Restore { get; }
    public NotificationsViewModel Notifications { get; }
    public SettingsViewModel Settings { get; }

    /// <summary>The configuration being edited (a copy of the service's one).</summary>
    public BackupConfig EditConfig { get; private set; } = new();
    public JsonNode? Status { get; private set; }
    public HashSet<string> SetSecrets { get; } = new(StringComparer.Ordinal);

    [ObservableProperty] public partial string? ConnectionError { get; set; }
    [ObservableProperty] public partial bool IsDirty { get; set; }
    public ObservableCollection<ValidationMessage> ValidationErrors { get; } = [];

    public event Action? ConfigReloaded;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            var cfg = await _client.CallAsync(IpcOps.ConfigGet).ConfigureAwait(true);
            EditConfig = ConfigJson.Parse(cfg!["config"]!.ToJsonString());
            Ui.Culture = BackupForge.Core.Resources.Texts.CultureFor(EditConfig.Language);
            SetSecrets.Clear();
            foreach (var s in (await _client.CallAsync(IpcOps.SecretsList).ConfigureAwait(true))?.AsArray() ?? [])
                SetSecrets.Add(s!["ref"]!.ToString());
            await RefreshStatusAsync().ConfigureAwait(true);
            ConnectionError = null;
            IsDirty = false;
            ValidationErrors.Clear();
            ConfigReloaded?.Invoke();
        }
        catch (IpcException ex) when (ex.Code == IpcErrorCodes.ProtocolMismatch)
        {
            ConnectionError = Ui.T("protocol_mismatch"); // CR-002 B4.: an older service after a GUI update
        }
        catch (Exception ex) when (ex is IpcUnavailableException or IpcException)
        {
            ConnectionError = ex.Message;
        }
    }

    public async Task RefreshStatusAsync()
    {
        try
        {
            Status = await _client.CallAsync(IpcOps.StatusGet).ConfigureAwait(true);
            Overview.Update(Status);
            ConnectionError = null;
        }
        catch (Exception ex) when (ex is IpcUnavailableException or IpcException)
        {
            ConnectionError = ex.Message;
        }
    }

    private DateTime _lastEventRefresh;

    private void OnEvent(IpcEvent e)
    {
        if (e.Event == "run.progress")
        {
            Overview.ApplyProgress(e.Data);
            return;
        }
        if ((DateTime.UtcNow - _lastEventRefresh).TotalMilliseconds < 300) return;
        _lastEventRefresh = DateTime.UtcNow;
        _ = RefreshStatusAsync();
    }

    public void MarkDirty() => IsDirty = true;

    /// <summary>
    /// The configuration as it would be saved: <see cref="EditConfig"/> with the edits of the pages (targets, notifications,
    /// settings). Jobs are changed by the wizard / the job table and are already in <see cref="EditConfig"/>.
    /// </summary>
    public BackupConfig BuildConfig()
    {
        var cfg = ConfigJson.Clone(EditConfig);
        Targets.ApplyTo(cfg);
        Notifications.ApplyTo(cfg);
        Settings.ApplyTo(cfg);
        return cfg;
    }

    /// <summary>Saves <paramref name="cfg"/> through the service (which validates); no dialogs — the caller shows the result.</summary>
    public async Task<(bool Ok, JsonArray? Errors, JsonArray? Warnings)> SaveConfigAsync(BackupConfig cfg)
    {
        try
        {
            var saved = await _client.CallAsync(IpcOps.ConfigSave, new JsonObject { ["config"] = ConfigJson.Serialize(cfg) }).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
            return (true, null, saved?["warnings"]?.AsArray());
        }
        catch (IpcException ex)
        {
            return (false, ex.Details?.AsArray() is { Count: > 0 } d ? d : new JsonArray(new JsonObject { ["path"] = "", ["message"] = ex.Message }), null);
        }
        catch (IpcUnavailableException ex)
        {
            return (false, new JsonArray(new JsonObject { ["path"] = "", ["message"] = ex.Message }), null);
        }
    }

    /// <summary>Collects every page and saves; validation errors are shown in words (CR-002 B5.), never as internal paths.</summary>
    [RelayCommand]
    public async Task<bool> SaveAsync()
    {
        var cfg = BuildConfig();
        ValidationErrors.Clear();
        var (ok, errors, warnings) = await SaveConfigAsync(cfg).ConfigureAwait(true);
        if (ok)
        {
            var w = warnings?.Select(x => Wizard.ErrorMap.Humanize(cfg, x?["path"]?.ToString() ?? "", x?["message"]?.ToString() ?? "", true).Text).ToList() ?? [];
            Dialogs.Info(w.Count == 0 ? Ui.T("saved") : Ui.T("saved") + Environment.NewLine + string.Join(Environment.NewLine, w));
            return true;
        }
        foreach (var d in errors ?? [])
        {
            var path = d?["path"]?.ToString() ?? "";
            var message = d?["message"]?.ToString() ?? "";
            ValidationErrors.Add(new ValidationMessage(path, message, Wizard.ErrorMap.Humanize(cfg, path, message).Text));
        }
        Dialogs.Error(Ui.T("save_failed") + Environment.NewLine + string.Join(Environment.NewLine, ValidationErrors.Select(v => v.Text)));
        return false;
    }

    public async Task<bool> SetSecretAsync(string reference, string value)
    {
        try
        {
            await _client.CallAsync(IpcOps.SecretsSet, new JsonObject { ["ref"] = reference, ["value"] = value }).ConfigureAwait(true);
            if (value.Length > 0) SetSecrets.Add(reference); else SetSecrets.Remove(reference);
            return true;
        }
        catch (Exception ex) when (ex is IpcException or IpcUnavailableException)
        {
            Dialogs.Error(ex.Message);
            return false;
        }
    }

    /// <summary>Runs an operation and turns service errors into a message box; returns null on failure.</summary>
    public async Task<JsonNode?> TryCallAsync(string op, JsonObject? args = null)
    {
        try { return await _client.CallAsync(op, args).ConfigureAwait(true) ?? new JsonObject(); }
        catch (Exception ex) when (ex is IpcException or IpcUnavailableException)
        {
            Dialogs.Error(ex.Message);
            return null;
        }
    }

    /// <summary>Like <see cref="TryCallAsync"/> but without a message box (wizard checks: a failure just shows nothing).</summary>
    public async Task<JsonNode?> TryCallQuietAsync(string op, JsonObject? args = null)
    {
        try { return await _client.CallAsync(op, args).ConfigureAwait(true) ?? new JsonObject(); }
        catch (Exception ex) when (ex is IpcException or IpcUnavailableException) { return null; }
    }

    /// <summary>Errors of one config path prefix (e.g. "jobs[2]"), in words.</summary>
    public string ErrorsFor(string prefix) =>
        string.Join(Environment.NewLine, ValidationErrors.Where(v => v.Path.StartsWith(prefix, StringComparison.Ordinal)).Select(v => v.Text));
}

/// <summary>A validation error: the internal path (for the mapping) and the text the user sees.</summary>
public sealed record ValidationMessage(string Path, string Message, string Text);
