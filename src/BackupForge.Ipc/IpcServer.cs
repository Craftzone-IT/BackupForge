using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace BackupForge.Ipc;

/// <summary>One client connection; the handler can push events to it after "events.subscribe".</summary>
public sealed class IpcConnection
{
    private readonly IpcFraming _framing;
    internal IpcConnection(IpcFraming framing, long number) { _framing = framing; Number = number; }
    public long Number { get; }
    public bool Subscribed { get; set; }

    public Task SendEventAsync(string name, JsonNode? data, CancellationToken ct) =>
        _framing.WriteLineAsync(JsonSerializer.Serialize(new IpcEvent { Event = name, Data = data }, IpcProtocol.Json), ct);
}

public delegate Task<JsonNode?> IpcHandler(IpcRequest request, IpcConnection connection, CancellationToken ct);

/// <summary>
/// Named pipe server. On Windows the pipe is created with an ACL for SYSTEM + Administrators only (SPEC 11.1, 14.).
/// Every request is handled independently; a slow request (job.run --wait) does not block others on the same connection.
/// </summary>
public sealed class IpcServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly IpcHandler _handler;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<IpcConnection> _connections = [];
    private readonly Lock _lock = new();
    private Task? _acceptLoop;
    private long _counter;

    public IpcServer(string pipeName, IpcHandler handler, ILogger logger)
    {
        _pipeName = pipeName;
        _handler = handler;
        _log = logger;
    }

    public void Start() => _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));

    public IReadOnlyList<IpcConnection> Subscribers { get { lock (_lock) return [.. _connections.Where(c => c.Subscribed)]; } }

    public async Task BroadcastAsync(string name, JsonNode? data)
    {
        foreach (var c in Subscribers)
        {
            try { await c.SendEventAsync(name, data?.DeepClone(), _cts.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
        }
    }

    private NamedPipeServerStream CreatePipe()
    {
        const PipeOptions options = PipeOptions.Asynchronous;
        if (OperatingSystem.IsWindows())
        {
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
            return NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, options, 0, 0, security);
        }
        return new NamedPipeServerStream(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, options);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(ex, "IPC: could not create / accept the pipe");
                try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                continue;
            }
            _ = Task.Run(() => HandleConnectionAsync(pipe, ct), CancellationToken.None);
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var framing = new IpcFraming(pipe);
        var conn = new IpcConnection(framing, Interlocked.Increment(ref _counter));
        lock (_lock) _connections.Add(conn);
        var inflight = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await framing.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;
                if (line.Length == 0) continue;
                inflight.RemoveAll(t => t.IsCompleted);
                inflight.Add(Task.Run(() => ProcessAsync(line, framing, conn, ct), CancellationToken.None));
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            lock (_lock) _connections.Remove(conn);
            try { await Task.WhenAll(inflight).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException or ObjectDisposedException) { }
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ProcessAsync(string line, IpcFraming framing, IpcConnection conn, CancellationToken ct)
    {
        IpcResponse response;
        long id = 0;
        try
        {
            var request = JsonSerializer.Deserialize<IpcRequest>(line, IpcProtocol.Json) ?? throw new IpcException(IpcErrorCodes.BadRequest, "empty request");
            id = request.Id;
            if (request.V != IpcProtocol.Version)
                throw new IpcException(IpcErrorCodes.ProtocolMismatch, $"protocol version mismatch: client {request.V}, service {IpcProtocol.Version} — use the bf.exe / GUI of the same version as the service");
            var result = await _handler(request, conn, ct).ConfigureAwait(false);
            response = new IpcResponse { Id = id, Ok = true, Result = result };
        }
        catch (IpcException ex)
        {
            response = new IpcResponse { Id = id, Ok = false, Error = new IpcError { Code = ex.Code, Message = ex.Message, Details = ex.Details } };
        }
        catch (JsonException ex)
        {
            response = new IpcResponse { Id = id, Ok = false, Error = new IpcError { Code = IpcErrorCodes.BadRequest, Message = "invalid JSON: " + ex.Message } };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogError(ex, "IPC request failed");
            response = new IpcResponse { Id = id, Ok = false, Error = new IpcError { Code = IpcErrorCodes.Internal, Message = ex.Message } };
        }
        try
        {
            await framing.WriteLineAsync(JsonSerializer.Serialize(response, IpcProtocol.Json), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _cts.Dispose();
    }
}
