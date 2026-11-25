using System.Text.Json.Nodes;
using BackupForge.Ipc;

namespace BackupForge.Gui.Core;

/// <summary>What the GUI needs from the service (IPC). Faked in the ViewModel tests.</summary>
public interface IServiceClient
{
    Task<JsonNode?> CallAsync(string op, JsonObject? args = null, CancellationToken ct = default);
    event Action<IpcEvent>? EventReceived;
}

/// <summary>The real client: one pipe connection, reconnects on demand.</summary>
public sealed class PipeServiceClient : IServiceClient, IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IpcClient? _client;

    public event Action<IpcEvent>? EventReceived;

    private async Task<IpcClient> GetAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_client is not null) return _client;
            var c = await IpcClient.ConnectAsync(ct: ct).ConfigureAwait(false);
            c.EventReceived += e => EventReceived?.Invoke(e);
            await c.CallAsync(IpcOps.EventsSubscribe, ct: ct).ConfigureAwait(false);
            _client = c;
            return c;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<JsonNode?> CallAsync(string op, JsonObject? args = null, CancellationToken ct = default)
    {
        try
        {
            return await (await GetAsync(ct).ConfigureAwait(false)).CallAsync(op, args, ct).ConfigureAwait(false);
        }
        catch (IpcUnavailableException)
        {
            if (_client is not null) await _client.DisposeAsync().ConfigureAwait(false);
            _client = null;
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Asks the user (message boxes / dialogs live in the WPF layer).</summary>
public interface IUserDialogs
{
    bool Confirm(string message);
    void Info(string message);
    void Error(string message);
    string? PickFolder(string? start);
    string? SaveFile(string defaultName, string filter);
    string? OpenFile(string filter);
    void ShowText(string title, string text, bool printable);
}

public sealed class NullDialogs : IUserDialogs
{
    public bool ConfirmResult { get; set; } = true;
    public List<string> Messages { get; } = [];
    public bool Confirm(string message) { Messages.Add("? " + message); return ConfirmResult; }
    public void Info(string message) => Messages.Add("i " + message);
    public void Error(string message) => Messages.Add("! " + message);
    public string? PickFolder(string? start) => PickedFolder;
    public string? SaveFile(string defaultName, string filter) => PickedFile;
    public string? OpenFile(string filter) => PickedFile;
    public void ShowText(string title, string text, bool printable) => Messages.Add($"T {title}: {text}");
    public string? PickedFolder { get; set; }
    public string? PickedFile { get; set; }
}
