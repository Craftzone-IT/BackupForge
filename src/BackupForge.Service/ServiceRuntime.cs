using System.Reflection;
using BackupForge.Core.Abstractions;
using BackupForge.Core.Config;
using BackupForge.Core.Execution;
using BackupForge.Core.History;
using BackupForge.Core.Logging;
using BackupForge.Core.Status;
using BackupForge.Ipc;
using Microsoft.Extensions.Logging;

namespace BackupForge.Service;

public sealed class ServiceOptions
{
    public string PipeName { get; init; } = IpcProtocol.PipeName;
    public TimeSpan CatchUpDelay { get; init; } = JobScheduler.DefaultCatchUpDelay;
    public TimeSpan ShutdownGrace { get; init; } = JobCoordinator.DefaultShutdownGrace;
    public bool StartIpc { get; init; } = true;
}

/// <summary>
/// Everything the service does, independent of the Windows service host (so it can run in tests and on Linux):
/// config, history, queue, scheduler, IPC, periodic housekeeping and the status file.
/// </summary>
public sealed class ServiceRuntime : IAsyncDisposable
{
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _log;
    private readonly IReadOnlyList<IJobEngine> _engines;
    private readonly ServiceOptions _options;
    private readonly List<IServiceComponent> _components = [];
    private IpcServer? _ipc;
    private DateTimeOffset _lastHousekeeping = DateTimeOffset.MinValue;

    public ServiceRuntime(DataPaths paths, ILoggerFactory loggers, IClock clock, IEnumerable<IJobEngine> engines, ISecretStore secrets, SecretMasker masker, ServiceOptions? options = null)
    {
        Paths = paths;
        _loggers = loggers;
        _log = loggers.CreateLogger("BackupForge.Service");
        Clock = clock;
        _engines = [.. engines];
        Secrets = secrets;
        Masker = masker;
        _options = options ?? new ServiceOptions();
    }

    public DataPaths Paths { get; }
    public IClock Clock { get; }
    public ISecretStore Secrets { get; }
    public SecretMasker Masker { get; }
    public HistoryStore History { get; private set; } = null!;
    public ConfigStore ConfigStore { get; private set; } = null!;
    public JobCoordinator Coordinator { get; private set; } = null!;
    public JobScheduler Scheduler { get; private set; } = null!;
    public DateTimeOffset Started { get; private set; }
    public ILoggerFactory Loggers => _loggers;
    public IpcServer? Ipc => _ipc;

    public static string AppVersion =>
        typeof(ServiceRuntime).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(ServiceRuntime).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    /// <summary>Additional features (status writer, notifier, watchdog …) hook in here.</summary>
    public void AddComponent(IServiceComponent component) => _components.Add(component);

    public async Task StartAsync(CancellationToken ct)
    {
        Started = Clock.UtcNow;
        Paths.EnsureCreated();
        History = new HistoryStore(Paths.HistoryDb);
        var interrupted = History.MarkUnfinishedInterrupted(Clock.UtcNow, "interrupted (service restart)");
        if (interrupted.Count > 0) _log.LogWarning("{Count} unfinished run(s) from the previous service run marked as interrupted", interrupted.Count);

        foreach (var r in Secrets.References) Masker.Register(Secrets.Get(r));
        ConfigStore = new ConfigStore(Paths, Secrets, History, Clock, _loggers.CreateLogger("BackupForge.Config"));
        var load = ConfigStore.Load("service");
        if (!load.Ok) _log.LogError("configuration error at start: {Error}", load.Error);

        Coordinator = new JobCoordinator(ConfigStore.Current, Clock, History, _engines, Secrets, Paths, Masker, _loggers.CreateLogger("BackupForge.Queue"));
        Scheduler = new JobScheduler(Clock, Coordinator, History, _loggers.CreateLogger("BackupForge.Scheduler")) { CatchUpDelay = _options.CatchUpDelay };
        Scheduler.Load(ConfigStore.Current, startup: true);

        foreach (var c in _components) await c.StartAsync(this, ct).ConfigureAwait(false);

        if (_options.StartIpc)
        {
            var handler = new ServiceIpcHandler(this);
            _ipc = new IpcServer(_options.PipeName, handler.HandleAsync, _loggers.CreateLogger("BackupForge.Ipc"));
            Coordinator.Changed += e => _ = handler.BroadcastAsync(e);
            _ipc.Start();
        }
        _log.LogInformation("BackupForge {Version} started, data: {Data}, {Jobs} job(s)", AppVersion, Paths.Root, ConfigStore.Current.Jobs.Count);
    }

    /// <summary>Periodic work (the host calls it every few seconds).</summary>
    public void Tick()
    {
        Scheduler.Tick();
        foreach (var c in _components)
        {
            try { c.Tick(this); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _log.LogError(ex, "{Component} tick failed", c.GetType().Name); }
        }
        var now = Clock.UtcNow;
        if (now - _lastHousekeeping > TimeSpan.FromHours(1))
        {
            _lastHousekeeping = now;
            Housekeeping(now);
        }
    }

    private void Housekeeping(DateTimeOffset now)
    {
        try
        {
            var cfg = ConfigStore.Current;
            History.Purge(now.AddDays(-cfg.General.HistoryRetentionDays));
            RunLogRetention.Cleanup(Paths, cfg.General.LogRetentionDays, now);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogWarning(ex, "housekeeping failed");
        }
    }

    public ConfigLoadResult ReloadConfig(string by)
    {
        var r = ConfigStore.Load(by);
        if (r.Ok) ApplyConfig();
        return r;
    }

    public ValidationResult SaveConfig(BackupConfig config, string by)
    {
        var v = ConfigStore.Save(config, by);
        if (v.IsValid) ApplyConfig();
        return v;
    }

    private void ApplyConfig()
    {
        Coordinator.UpdateConfig(ConfigStore.Current);
        Scheduler.Load(ConfigStore.Current, startup: false);
        foreach (var c in _components) c.ConfigChanged(this);
    }

    public StatusProviders StatusProviders { get; set; } = new();
    public Components.Notifier? Notifier { get; set; }
    public Components.TargetProbe? TargetProbe { get; set; }

    public StatusDocument BuildStatus() => StatusBuilder.Build(new StatusInputs
    {
        Config = ConfigStore.Current,
        History = History,
        Clock = Clock,
        ServiceStarted = Started,
        AppVersion = AppVersion,
        ConfigOk = ConfigStore.IsFileValid,
        ConfigError = ConfigStore.Error,
        ConfigChanged = ConfigStore.LastChanged,
        NextRun = id => Scheduler.NextRun(id),
        Running = Coordinator.Running,
        DisabledSince = ConfigStore.DisabledSince,
        Providers = StatusProviders,
    });

    public async Task StopAsync()
    {
        _log.LogInformation("BackupForge stopping");
        if (_ipc is not null) await _ipc.DisposeAsync().ConfigureAwait(false);
        _ipc = null;
        if (Coordinator is not null) await Coordinator.ShutdownAsync(_options.ShutdownGrace).ConfigureAwait(false);
        foreach (var c in _components)
        {
            try { await c.StopAsync(this).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _log.LogWarning(ex, "{Component} stop failed", c.GetType().Name); }
        }
        _log.LogInformation("BackupForge stopped");
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}

/// <summary>A pluggable part of the service (status writer, notifications, watchdog, heartbeat …).</summary>
public interface IServiceComponent
{
    Task StartAsync(ServiceRuntime runtime, CancellationToken ct) => Task.CompletedTask;
    void Tick(ServiceRuntime runtime) { }
    void ConfigChanged(ServiceRuntime runtime) { }
    Task StopAsync(ServiceRuntime runtime) => Task.CompletedTask;
}
