using BackupForge.Core.Abstractions;
using BackupForge.Core.Config;
using BackupForge.Core.Execution;
using BackupForge.Core.Logging;
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

    public async Task<RuntimeHarness> StartAsync(IEnumerable<IJobEngine>? engines = null)
    {
        Runtime = new ServiceRuntime(Paths, NullLoggerFactory.Instance, Clock, engines ?? [Engine], Secrets, new SecretMasker(),
            new ServiceOptions { PipeName = Pipe, CatchUpDelay = TimeSpan.FromMinutes(2) });
        await Runtime.StartAsync(CancellationToken.None);
        return this;
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
