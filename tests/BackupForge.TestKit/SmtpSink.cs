using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BackupForge.TestKit;

/// <summary>A minimal plain-SMTP server that accepts every message (tests only, loopback).</summary>
public sealed class SmtpSink : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public SmtpSink()
    {
        _listener.Start();
        _loop = Task.Run(() => AcceptAsync(_cts.Token));
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public List<string> Messages { get; } = [];
    public List<string> AuthLines { get; } = [];

    private async Task AcceptAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct); }
            catch (Exception) when (ct.IsCancellationRequested) { return; }
            _ = Task.Run(() => HandleAsync(client, ct), ct);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        var stream = client.GetStream();
        var reader = new StreamReader(stream, Encoding.ASCII);
        var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
        await writer.WriteLineAsync("220 sink ready");
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            var cmd = line.Length >= 4 ? line[..4].ToUpperInvariant() : line.ToUpperInvariant();
            switch (cmd)
            {
                case "EHLO":
                    await writer.WriteLineAsync("250-sink");
                    await writer.WriteLineAsync("250 AUTH PLAIN LOGIN");
                    break;
                case "HELO":
                    await writer.WriteLineAsync("250 sink");
                    break;
                case "AUTH":
                    lock (AuthLines) AuthLines.Add(line);
                    await writer.WriteLineAsync("235 ok");
                    break;
                case "DATA":
                    await writer.WriteLineAsync("354 go");
                    var sb = new StringBuilder();
                    while (await reader.ReadLineAsync(ct) is { } d && d != ".") sb.AppendLine(d);
                    lock (Messages) Messages.Add(sb.ToString());
                    await writer.WriteLineAsync("250 queued");
                    break;
                case "QUIT":
                    await writer.WriteLineAsync("221 bye");
                    return;
                default:
                    await writer.WriteLineAsync("250 ok");
                    break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        try { await _loop; } catch (OperationCanceledException) { }
    }
}
