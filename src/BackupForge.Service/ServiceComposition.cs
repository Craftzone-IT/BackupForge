using BackupForge.Core.Abstractions;
using BackupForge.Core.Execution;
using BackupForge.Core.Logging;
using BackupForge.Core.Notifications;
using BackupForge.Core.Status;
using BackupForge.Engine.Files;
using BackupForge.Engine.Restic;
using BackupForge.Engine.Sql;
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
    public HttpMessageHandler? HttpHandler { get; init; }
    public ResticBinary Restic { get; init; } = ResticBinary.Default();
    public IProcessRunner Processes { get; init; } = new SystemProcessRunner();
    public ISqlSessionFactory Sql { get; init; } = new SqlClientSessionFactory();
    public IStagingPermissions StagingPermissions { get; init; } = NoStagingPermissions.Instance;
    public Func<DataPaths, ISecretStore> Secrets { get; init; } = _ => new InMemorySecretStore();

    public static Platform Current()
    {
        if (!OperatingSystem.IsWindows()) return new Platform();
        var connector = new Windows.WNetConnector();
        return new Platform
        {
            // CR-003 2.: every UNC access to a credentialed SMB server must happen inside a lease of this connector
            FileSystem = new LeaseGuardFileSystem(PhysicalFileSystem.Instance)
            {
                HasLease = connector.Manager.HasLease,
#if DEBUG
                ThrowOnViolation = true,
#else
                ThrowOnViolation = Environment.GetEnvironmentVariable("BF_LEASE_GUARD_THROW") == "1",
#endif
            },
            Connector = connector,
            Vss = new Windows.VssSnapshotProvider(),
            Events = new Windows.WindowsEventSink(),
            StagingPermissions = new Windows.StagingAcl(),
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

    public static IReadOnlyList<IJobEngine> Engines(Platform platform, SecretMasker masker)
    {
        if (TestEngine.Enabled) return [new TestEngine()];
        return
        [
            new FileJobEngine(platform.FileSystem, platform.Connector, platform.Vss, platform.FileEngine),
            new FileRestoreEngine(platform.FileSystem, platform.Connector),
            new ResticEngine(platform.Restic, platform.Processes, masker),
            new SqlEngine(platform.Sql, platform.FileSystem, platform.StagingPermissions),
        ];
    }

    public static ServiceRuntime CreateRuntime(DataPaths paths, ILoggerFactory loggers, IClock clock, SecretMasker masker, ServiceOptions? options = null, Platform? platform = null)
    {
        platform ??= Platform.Current();
        var engines = Engines(platform, masker);
        var secrets = platform.Secrets(paths);
        if (OperatingSystem.IsWindows() && secrets is Windows.DpapiSecretStore { AclRepaired: true })
            loggers.CreateLogger("BackupForge.Secrets").LogInformation("secrets.dat: access list reset to SYSTEM only");
        var rt = new ServiceRuntime(paths, loggers, clock, engines, secrets, masker, options);
        Attach(rt, platform, engines);
        return rt;
    }

    /// <summary>Adds the standard components and status providers to a runtime.</summary>
    public static void Attach(ServiceRuntime rt, Platform platform, IReadOnlyList<IJobEngine> engines)
    {
        rt.FileSystem = platform.FileSystem;
        rt.Connector = platform.Connector;
        if (platform.FileSystem is LeaseGuardFileSystem guard)
        {
            var log = rt.Loggers.CreateLogger("BackupForge.Targets");
            guard.NeedsLease = rt.NeedsLease;
            guard.Warn = m => log.LogWarning("{Message}", m);
        }
        var notifier = new Notifier(platform.Mail, platform.Events);
        var probe = new TargetProbe(platform.FileSystem, platform.Connector);
        rt.AddComponent(new LifecycleEvents(platform.Events, platform.Vss));
        var restic = new ResticComponent(platform.Restic, platform.Processes, platform.Events);
        rt.AddComponent(restic);
        rt.Restic = restic;
        var sql = new SqlSupport(platform.Sql);
        rt.AddComponent(sql);
        rt.Sql = sql;
        rt.AddComponent(probe);
        rt.AddComponent(notifier);
        var reporter = new WeeklyReporter(notifier);
        rt.AddComponent(reporter);
        rt.AddComponent(new Heartbeat(platform.HttpHandler, platform.Events));
        var status = new StatusWriter();
        rt.AddComponent(status);
        rt.Notifier = notifier;
        rt.Reporter = reporter;
        rt.StatusWriter = status;
        rt.TargetProbe = probe;
        rt.StatusProviders = new StatusProviders
        {
            Target = probe.Get,
            Versions = job => VersionsState.Read(rt.History, rt.ConfigStore.Current, job), // never touches the target (CR-003 2.)
            Notifications = notifier.Status,
            ResticJob = job => new BackupForge.Core.Status.ResticJobStatus
            {
                LastSnapshot = rt.History.GetValue(ResticState.LastSnapshotKey(job.Id)),
                Snapshots = int.TryParse(rt.History.GetValue(ResticState.SnapshotCountKey(job.Id)), out var n) ? n : null,
            },
            Sql = sql.Status,
            Repo = _ => new BackupForge.Core.Status.RepoStatus { ResticVersion = rt.History.GetValue(ResticState.VersionKey) },
        };
    }
}
