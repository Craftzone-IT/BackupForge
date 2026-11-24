using BackupForge.Core.Abstractions;
using BackupForge.Core.Execution;
using BackupForge.Core.Logging;
using BackupForge.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Usage: BackupForge.Service.exe [--data <dir>]   (as a Windows service, or interactively for development)
//        [--pipe <name>] [--test-engine]   (integration tests only)
string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
if (Arg("--data") is { } dataDir) Environment.SetEnvironmentVariable(DataPaths.DataDirEnvVar, dataDir);
if (Arg("--pipe") is { } pipe) Environment.SetEnvironmentVariable(BackupForge.Ipc.IpcProtocol.PipeEnvVar, pipe);
if (args.Contains("--test-engine")) Environment.SetEnvironmentVariable(TestEngine.EnvVar, "1");

var paths = DataPaths.Default();
if (args.Length > 0 && args[0] is "--setup-install" or "--setup-uninstall") return SetupHelper.Run(args, paths, Console.Out);
paths.EnsureCreated();
var masker = new SecretMasker();
var clock = SystemClock.Instance;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "BackupForge");
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(75));
builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Logging.AddProvider(new FileLoggerProvider(paths.LogsDir, "service", 30, clock, masker));
if (!Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService()) builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
ServiceComposition.ConfigureLogging(builder.Logging);

builder.Services.AddSingleton(sp => ServiceComposition.CreateRuntime(paths, sp.GetRequiredService<ILoggerFactory>(), clock, masker));
builder.Services.AddHostedService<Worker>();

await builder.Build().RunAsync().ConfigureAwait(false);
return 0;
