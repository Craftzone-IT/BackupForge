using BackupForge.Core.Abstractions;
using BackupForge.Core.Execution;
using BackupForge.Core.Logging;
using BackupForge.Core.Notifications;
using BackupForge.Core.Status;
using BackupForge.Engine.Files;
using BackupForge.Service.Components;
using Microsoft.Extensions.Logging;

namespace BackupForge.Service;

/// <summary>Platform services the runtime needs (real ones on Windows, neutral ones elsewhere / in tests).</summary>
public sealed class Platform
{
    public IFileSystem FileSystem { get; init; } = PhysicalFileSystem.Instance;
    public ITargetConnector Connector { get; init; } = NoOpTargetConnector.Instance;
    public ISnapshotProvider Vss { get; init; } = NoSnapshotProvider.Instance;
    public IEventSink Events { get; init; } = NullEventSink.Instance;
    public IMailSender Mail { get; init; } = new MailKitSender();
    public FileEngineOptions FileEngine { get; init; } = new();
    public Func<DataPaths, ISecretStore> Secrets { get; init; } = _ => new InMemorySecretStore();

    public static Platform Current()
    {
        if (!OperatingSystem.IsWindows()) return new Platform();
        return new Platform
        {
            Connector = new Windows.WNetConnector(),
            Vss = new Windows.VssSnapshotProvider(),
            Events = new Windows.WindowsEventSink(),
            Secrets = p => OperatingSystem.IsWindows() ? new Windows.DpapiSecretStore(p.SecretsFile) : new InMemorySecretStore(),
        };
    }
}

/// <summary>Wires engines, secret store and components together (shared by Program and the tests).</summary>
public static class ServiceComposition
{
    public static void ConfigureLogging(ILoggingBuilder logging)
    {
    }

    public static IReadOnlyList<IJobEngine> Engines(Platform platform)
    {
        if (TestEngine.Enabled) return [new TestEngine()];
        return [new FileJobEngine(platform.FileSystem, platform.Connector, platform.Vss, platform.FileEngine)];
    }

    public static ServiceRuntime CreateRuntime(DataPaths paths, ILoggerFactory loggers, IClock clock, SecretMasker masker, ServiceOptions? options = null, Platform? platform = null)
    {
        platform ??= Platform.Current();
        var engines = Engines(platform);
        var rt = new ServiceRuntime(paths, loggers, clock, engines, platform.Secrets(paths), masker, options);
        Attach(rt, platform, engines);
        return rt;
    }

    /// <summary>Adds the standard components and status providers to a runtime.</summary>
    public static void Attach(ServiceRuntime rt, Platform platform, IReadOnlyList<IJobEngine> engines)
    {
        var notifier = new Notifier(platform.Mail, platform.Events);
        var probe = new TargetProbe(platform.FileSystem, platform.Connector);
        rt.AddComponent(new LifecycleEvents(platform.Events, platform.Vss));
        rt.AddComponent(probe);
        rt.AddComponent(notifier);
        rt.Notifier = notifier;
        rt.TargetProbe = probe;
        var files = engines.OfType<FileJobEngine>().FirstOrDefault();
        rt.StatusProviders = new StatusProviders
        {
            Target = probe.Get,
            Versions = job => files?.VersionsInfo(rt.ConfigStore.Current, job),
            Notifications = notifier.Status,
        };
    }
}
