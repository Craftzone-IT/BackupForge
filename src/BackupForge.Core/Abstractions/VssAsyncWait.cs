using System.Diagnostics;

namespace BackupForge.Core.Abstractions;

/// <summary>The three IVssAsync methods the wait loop needs (an interface so the loop is testable without COM).</summary>
public interface IVssAsyncOperation
{
    int Cancel();
    int Wait(uint milliseconds);
    int QueryStatus(out int status);
}

/// <summary>Thrown by the VSS wait loop / snapshot code with the HRESULT and the step trace (CR-002 A1).</summary>
public sealed class VssStepException(string message, int hresult, IReadOnlyList<string> trace) : Exception(message)
{
    public int Code { get; } = hresult;
    public IReadOnlyList<string> Trace { get; } = trace;
}

/// <summary>
/// A snapshot was created, but VSS returned no device path for it: a VSS engine (interop) problem, not "VSS unavailable".
/// The engine logs <see cref="Trace"/> at Error level (CR-002 A1. 4.).
/// </summary>
public sealed class VssSnapshotIncompleteException(string message, IReadOnlyList<string> trace) : Exception(message)
{
    public IReadOnlyList<string> Trace { get; } = trace;
}

/// <summary>
/// The IVssAsync polling loop (CR-002 A1.): only VSS_S_ASYNC_FINISHED means done. S_OK (0) from QueryStatus is NOT treated
/// as finished (the operation may still be running) — the loop keeps polling until FINISHED, CANCELLED, an error, or the deadline.
/// A Wait() time-out (any success code, or HRESULT_FROM_WIN32(WAIT_TIMEOUT)) just means "poll again".
/// </summary>
public static class VssAsyncWait
{
    public const int VSS_S_ASYNC_PENDING = 0x00042309;
    public const int VSS_S_ASYNC_FINISHED = 0x0004230A;
    public const int VSS_S_ASYNC_CANCELLED = 0x0004230B;
    public const int WAIT_TIMEOUT_HR = unchecked((int)0x80070102);
    public const int E_PENDING = unchecked((int)0x8000000A);
    public const int VSS_E_HOLD_WRITES_TIMEOUT = unchecked((int)0x80042314);

    /// <summary>Waits for the operation; returns normally only on VSS_S_ASYNC_FINISHED. Every observed status goes to <paramref name="trace"/>.</summary>
    public static void Run(IVssAsyncOperation op, string what, TimeSpan limit, CancellationToken ct, List<string> trace, uint pollMs = 1000)
    {
        var sw = Stopwatch.StartNew();
        int? lastStatus = null;
        while (true)
        {
            if (ct.IsCancellationRequested)
            {
                op.Cancel();
                trace.Add($"{what}: cancelled by caller after {sw.Elapsed.TotalSeconds:0.0} s");
                ct.ThrowIfCancellationRequested();
            }
            if (sw.Elapsed > limit)
            {
                op.Cancel();
                trace.Add($"{what}: no result within {limit.TotalSeconds:0} s (last status {Hex(lastStatus)})");
                throw new VssStepException($"{what} did not finish within {limit.TotalSeconds:0} s", VSS_E_HOLD_WRITES_TIMEOUT, trace);
            }
            var hrWait = op.Wait(pollMs);
            if (hrWait < 0 && hrWait != WAIT_TIMEOUT_HR && hrWait != E_PENDING)
            {
                trace.Add($"{what}: Wait failed {Hex(hrWait)} after {sw.Elapsed.TotalSeconds:0.0} s");
                throw new VssStepException($"{what} (wait) failed: {Hex(hrWait)}", hrWait, trace);
            }
            var hrQuery = op.QueryStatus(out var status);
            if (hrQuery < 0)
            {
                trace.Add($"{what}: QueryStatus failed {Hex(hrQuery)}");
                throw new VssStepException($"{what} (status) failed: {Hex(hrQuery)}", hrQuery, trace);
            }
            if (status != lastStatus) trace.Add($"{what}: status {Hex(status)} {Name(status)} at {sw.Elapsed.TotalSeconds:0.0} s");
            lastStatus = status;
            switch (status)
            {
                case VSS_S_ASYNC_FINISHED:
                    return;
                case VSS_S_ASYNC_CANCELLED:
                    throw new OperationCanceledException(what + " cancelled");
                case VSS_S_ASYNC_PENDING:
                case 0: // S_OK: not a completion code — keep polling
                    continue;
                default:
                    if (status < 0) throw new VssStepException($"{what} failed: {Hex(status)}", status, trace);
                    continue; // other success codes: keep polling
            }
        }
    }

    public static string Hex(int? hr) => hr is { } v ? $"0x{v:X8}" : "-";

    public static string Name(int status) => status switch
    {
        VSS_S_ASYNC_PENDING => "(PENDING)",
        VSS_S_ASYNC_FINISHED => "(FINISHED)",
        VSS_S_ASYNC_CANCELLED => "(CANCELLED)",
        0 => "(S_OK)",
        _ => "",
    };
}
