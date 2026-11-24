using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BackupForge.Ipc;

/// <summary>Client side of the named pipe (bf.exe, GUI). Thread-safe; requests may run concurrently.</summary>
public sealed class IpcClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly IpcFraming _framing;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<IpcResponse>> _waiting = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _readLoop;
    private long _nextId;

    private IpcClient(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _framing = new IpcFraming(pipe);
    }

    public event Action<IpcEvent>? EventReceived;

    public static async Task<IpcClient> ConnectAsync(string? pipeName = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var pipe = new NamedPipeClientStream(".", pipeName ?? IpcProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync((int)(timeout ?? TimeSpan.FromSeconds(3)).TotalMilliseconds, ct).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw new IpcUnavailableException("the BackupForge service is not reachable (is it running?)", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw new IpcUnavailableException("access denied to the BackupForge service — run as Administrator", ex);
        }
        catch (IOException ex)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw new IpcUnavailableException("the BackupForge service is not reachable: " + ex.Message, ex);
        }
        var c = new IpcClient(pipe);
        c._readLoop = Task.Run(() => c.ReadLoopAsync(c._cts.Token));
        return c;
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await _framing.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;
                var node = JsonNode.Parse(line) as JsonObject;
                if (node is null) continue;
                if (node.ContainsKey("event"))
                {
                    var ev = node.Deserialize<IpcEvent>(IpcProtocol.Json);
                    if (ev is not null) EventReceived?.Invoke(ev);
                    continue;
                }
                var resp = node.Deserialize<IpcResponse>(IpcProtocol.Json);
                if (resp is not null && _waiting.TryRemove(resp.Id, out var tcs)) tcs.TrySetResult(resp);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or JsonException) { }
        foreach (var (_, tcs) in _waiting) tcs.TrySetException(new IpcUnavailableException("connection to the service was lost"));
        _waiting.Clear();
    }

    /// <summary>Sends a request and returns the result node, or throws <see cref="IpcException"/> on an error response.</summary>
    public async Task<JsonNode?> CallAsync(string op, object? args = null, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<IpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[id] = tcs;
        var argsNode = args switch
        {
            null => null,
            JsonObject o => o,
            _ => JsonSerializer.SerializeToNode(args, IpcProtocol.Json) as JsonObject,
        };
        var request = new IpcRequest { Id = id, Op = op, Args = argsNode };
        try
        {
            await _framing.WriteLineAsync(JsonSerializer.Serialize(request, IpcProtocol.Json), ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            _waiting.TryRemove(id, out _);
            throw new IpcUnavailableException("connection to the service was lost", ex);
        }
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        var resp = await tcs.Task.ConfigureAwait(false);
        if (!resp.Ok) throw new IpcException(resp.Error?.Code ?? IpcErrorCodes.Internal, resp.Error?.Message ?? "error", resp.Error?.Details);
        return resp.Result;
    }

    public async Task<T?> CallAsync<T>(string op, object? args = null, CancellationToken ct = default)
    {
        var node = await CallAsync(op, args, ct).ConfigureAwait(false);
        return node is null ? default : node.Deserialize<T>(IpcProtocol.Json);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        await _pipe.DisposeAsync().ConfigureAwait(false);
        if (_readLoop is not null)
        {
            try { await _readLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _cts.Dispose();
    }
}
