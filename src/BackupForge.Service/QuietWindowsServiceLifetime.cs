using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BackupForge.Service;

/// <summary>
/// CR-002 A7.: ServiceBase.AutoLog writes "Service started / stopped successfully" with event id 0 into the Application log.
/// BackupForge writes only the ids of docs/EVENT-IDS.md (1000 / 1001 are its own start / stop events); everything else goes
/// to the file log.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class QuietWindowsServiceLifetime : WindowsServiceLifetime
{
    public QuietWindowsServiceLifetime(IHostEnvironment environment, IHostApplicationLifetime applicationLifetime, ILoggerFactory loggerFactory,
        IOptions<HostOptions> optionsAccessor, IOptions<WindowsServiceLifetimeOptions> windowsServiceOptionsAccessor)
        : base(environment, applicationLifetime, loggerFactory, optionsAccessor, windowsServiceOptionsAccessor)
    {
        AutoLog = false;
    }
}
