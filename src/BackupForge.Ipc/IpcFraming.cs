using System.Text;

namespace BackupForge.Ipc;

/// <summary>Newline-delimited UTF-8 JSON over a stream.</summary>
internal sealed class IpcFraming(Stream stream)
{
    private readonly StreamReader _reader = new(stream, new UTF8Encoding(false), false, 1 << 16, leaveOpen: true);
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        var line = await _reader.ReadLineAsync(ct).ConfigureAwait(false);
        if (line is not null && line.Length > IpcProtocol.MaxMessageBytes) throw new IOException("IPC message too large");
        return line;
    }

    public async Task WriteLineAsync(string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
