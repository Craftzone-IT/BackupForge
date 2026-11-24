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

    /// <summary>Collects every page into <see cref="EditConfig"/> and saves it; validation errors are shown field by field.</summary>
    [RelayCommand]
    public async Task<bool> SaveAsync()
    {
        Jobs.ApplyTo(EditConfig);
        Targets.ApplyTo(EditConfig);
        Notifications.ApplyTo(EditConfig);
        Settings.ApplyTo(EditConfig);
        ValidationErrors.Clear();
        try
        {
            await _client.CallAsync(IpcOps.ConfigSave, new JsonObject { ["config"] = ConfigJson.Serialize(EditConfig) }).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
            Dialogs.Info(Ui.T("saved"));
            return true;
        }
        catch (IpcException ex)
        {
            foreach (var d in ex.Details?.AsArray() ?? [])
                ValidationErrors.Add(new ValidationMessage(d!["path"]?.ToString() ?? "", d["message"]?.ToString() ?? ""));
            if (ValidationErrors.Count == 0) ValidationErrors.Add(new ValidationMessage("", ex.Message));
            Dialogs.Error(Ui.T("save_failed") + Environment.NewLine + string.Join(Environment.NewLine, ValidationErrors.Select(v => $"{v.Path}: {v.Message}")));
            return false;
        }
        catch (IpcUnavailableException ex)
        {
            Dialogs.Error(ex.Message);
            return false;
        }
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

    /// <summary>Errors of one config path prefix (e.g. "jobs[2]") for the field-level display.</summary>
    public string ErrorsFor(string prefix) =>
        string.Join(Environment.NewLine, ValidationErrors.Where(v => v.Path.StartsWith(prefix, StringComparison.Ordinal)).Select(v => $"{v.Path[prefix.Length..].TrimStart('.')}: {v.Message}"));
}

public sealed record ValidationMessage(string Path, string Message);
