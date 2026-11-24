using System.Diagnostics;
using BackupForge.Core.Config;
using BackupForge.Core.Execution;
using BackupForge.Ipc;
using BackupForge.TestKit;

namespace BackupForge.Integration.Tests;

/// <summary>Starts BackupForge.Service as a real child process (works on Linux and Windows).</summary>
public sealed class ServiceProcess : IAsyncDisposable
{
    public TempDir Dir { get; } = new();
    public DataPaths Paths { get; }
    public string Pipe { get; } = "bf-it-" + Guid.NewGuid().ToString("N")[..10];
    private Process? _process;

    public ServiceProcess(BackupConfig config)
    {
        Paths = new DataPaths(Dir.Path);
        Paths.EnsureCreated();
        AtomicFile.Write(Paths.ConfigFile, ConfigJson.ToUtf8(config));
    }

    public static string BinDir => AppContext.BaseDirectory;

    public static ProcessStartInfo Dotnet(string dll, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(Path.Combine(BinDir, dll));
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    public async Task StartAsync()
    {
        var psi = Dotnet("BackupForge.Service.dll", "--data", Paths.Root, "--pipe", Pipe, "--test-engine");
        psi.Environment["BACKUPFORGE_TEST_ENGINE_MS"] = "200";
        _process = Process.Start(psi)!;
        _process.OutputDataReceived += (_, _) => { };
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        await WaitForPipeAsync(Pipe, TimeSpan.FromSeconds(30));
    }

    public static async Task WaitForPipeAsync(string pipe, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                await using var c = await IpcClient.ConnectAsync(pipe, TimeSpan.FromSeconds(1));
                await c.CallAsync(IpcOps.Hello);
                return;
            }
            catch (IpcUnavailableException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(250);
            }
        }
    }

    public async Task<(int Code, string Out, string Err)> Bf(params string[] args)
    {
        var psi = Dotnet("bf.dll", args);
        psi.Environment[IpcProtocol.PipeEnvVar] = Pipe;
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        return (p.ExitCode, await o, await e);
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }
        _process?.Dispose();
        Dir.Dispose();
    }
}
