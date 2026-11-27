using BackupForge.Core.Abstractions;
using BackupForge.Core.Config;
using BackupForge.Core.Execution;
using BackupForge.Core.Logging;
using BackupForge.Core.Notifications;
using BackupForge.Ipc;
using BackupForge.TestKit;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupForge.Service.Tests;

/// <summary>An in-process service runtime with its own data folder and pipe name.</summary>
public sealed class RuntimeHarness : IAsyncDisposable
{
    public TempDir Dir { get; } = new();
    public DataPaths Paths { get; }
    public ManualClock Clock { get; } = new(new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero));
    public FakeEngine Engine { get; } = new() { AutoComplete = true };
    public string Pipe { get; } = "bf-test-" + Guid.NewGuid().ToString("N")[..10];
    public InMemorySecretStore Secrets { get; } = new();
    public ServiceRuntime Runtime { get; private set; } = null!;

    public RuntimeHarness(BackupConfig? config = null)
    {
        Paths = new DataPaths(Dir.Path);
        Paths.EnsureCreated();
        if (config is not null) AtomicFile.Write(Paths.ConfigFile, ConfigJson.ToUtf8(config));
    }

    public FakeMailSender Mail { get; } = new();
    public MemoryEventSink Events { get; } = new();
    public SecretMasker Masker { get; } = new();

    public async Task<RuntimeHarness> StartAsync(IEnumerable<IJobEngine>? engines = null, bool components = true, IFileSystem? fileSystem = null, ITargetConnector? connector = null)
    {
        var list = (engines ?? [Engine]).ToList();
        Runtime = new ServiceRuntime(Paths, NullLoggerFactory.Instance, Clock, list, Secrets, Masker,
            new ServiceOptions { PipeName = Pipe, CatchUpDelay = TimeSpan.FromMinutes(2) });
        if (components)
            ServiceComposition.Attach(Runtime, new Platform
            {
                Mail = Mail, Events = Events,
                FileSystem = fileSystem ?? PhysicalFileSystem.Instance,
                Connector = connector ?? NoOpTargetConnector.Instance,
            }, list);
        await Runtime.StartAsync(CancellationToken.None);
        return this;
    }

    /// <summary>Ticks until the notifier's outbox is empty (or the attempts are used up).</summary>
    public async Task FlushMailAsync()
    {
        for (var i = 0; i < 50; i++)
        {
            Runtime.Tick();
            await Runtime.Notifier!.Sending;
            if (Runtime.Notifier.PendingCount == 0) return;
            await Task.Delay(10);
        }
    }

    public Task<IpcClient> Connect() => IpcClient.ConnectAsync(Pipe, TimeSpan.FromSeconds(5));

    public async Task<(int Code, string Out, string Err)> Bf(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        Cli.BfCli.Connect = Connect;
        var code = await Cli.BfCli.RunAsync(args, o, e, new StringReader(""));
        return (code, o.ToString(), e.ToString());
    }

    public async ValueTask DisposeAsync()
    {
        if (Runtime is not null) await Runtime.StopAsync();
        Dir.Dispose();
    }
}

public sealed class FakeMailSender : IMailSender
{
    public List<MailMessage> Sent { get; } = [];
    public int FailNext { get; set; }
    public int Attempts { get; private set; }

    public Task SendAsync(EmailSettings settings, string? password, MailMessage message, CancellationToken ct)
    {
        Attempts++;
        if (FailNext > 0) { FailNext--; throw new IOException("smtp connection refused"); }
        lock (Sent) Sent.Add(message);
        return Task.CompletedTask;
    }
}
