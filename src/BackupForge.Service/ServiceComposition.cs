using BackupForge.Core.Abstractions;
using BackupForge.Core.Execution;
using BackupForge.Core.Logging;
using Microsoft.Extensions.Logging;

namespace BackupForge.Service;

/// <summary>Wires engines, secret store and components together (shared by Program and the tests).</summary>
public static class ServiceComposition
{
    public static void ConfigureLogging(ILoggingBuilder logging)
    {
        // Event log sink (Warning+) is added in F2 together with the event ids.
    }

    public static IReadOnlyList<IJobEngine> Engines(ILoggerFactory loggers)
    {
        if (TestEngine.Enabled) return [new TestEngine()];
        return [];
    }

    public static ISecretStore CreateSecretStore(DataPaths paths) => new InMemorySecretStore();

    public static ServiceRuntime CreateRuntime(DataPaths paths, ILoggerFactory loggers, IClock clock, SecretMasker masker, ServiceOptions? options = null)
    {
        var rt = new ServiceRuntime(paths, loggers, clock, Engines(loggers), CreateSecretStore(paths), masker, options);
        return rt;
    }
}
