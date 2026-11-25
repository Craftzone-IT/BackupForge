namespace BackupForge.Core.Abstractions;

public sealed class ProcessSpec
{
    public required string FileName { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    /// <summary>Extra environment variables (secrets go here, never into Arguments).</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
    public string? WorkingDirectory { get; init; }
    public string? StandardInput { get; init; }
}

public sealed record ProcessResult(int ExitCode, IReadOnlyList<string> StdErrTail, bool Killed);

/// <summary>Starts child processes and streams their output line by line.</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessSpec spec, Action<string>? onStdOut, Action<string>? onStdErr, CancellationToken ct);
}

public sealed class SystemProcessRunner : IProcessRunner
{
    public const int StdErrTailLines = 50;

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, Action<string>? onStdOut, Action<string>? onStdErr, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(spec.FileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = spec.StandardInput is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        if (spec.WorkingDirectory is not null) psi.WorkingDirectory = spec.WorkingDirectory;
        foreach (var a in spec.Arguments) psi.ArgumentList.Add(a);
        foreach (var (k, v) in spec.Environment) psi.Environment[k] = v;

        var tail = new Queue<string>();
        using var p = new System.Diagnostics.Process { StartInfo = psi };
        var outDone = new TaskCompletionSource();
        var errDone = new TaskCompletionSource();
        p.OutputDataReceived += (_, e) => { if (e.Data is null) outDone.TrySetResult(); else onStdOut?.Invoke(e.Data); };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) { errDone.TrySetResult(); return; }
            lock (tail) { tail.Enqueue(e.Data); while (tail.Count > StdErrTailLines) tail.Dequeue(); }
            onStdErr?.Invoke(e.Data);
        };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (spec.StandardInput is not null)
        {
            await p.StandardInput.WriteAsync(spec.StandardInput).ConfigureAwait(false);
            p.StandardInput.Close();
        }

        var killed = false;
        try
        {
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            killed = true;
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        await Task.WhenAll(outDone.Task, errDone.Task).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
        string[] tailArr;
        lock (tail) tailArr = [.. tail];
        return new ProcessResult(p.ExitCode, tailArr, killed);
    }
}
