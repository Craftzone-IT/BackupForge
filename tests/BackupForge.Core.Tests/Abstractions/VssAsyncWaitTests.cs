using BackupForge.Core.Abstractions;

namespace BackupForge.Core.Tests.Abstractions;

/// <summary>CR-002 A1.: the IVssAsync polling loop — only VSS_S_ASYNC_FINISHED is completion.</summary>
public class VssAsyncWaitTests
{
    private sealed class FakeAsync(params (int Wait, int Status)[] steps) : IVssAsyncOperation
    {
        private int _i = -1;
        public int Polls => _i + 1;
        public bool Cancelled { get; private set; }
        public int Cancel() { Cancelled = true; return 0; }
        public int Wait(uint milliseconds) { _i = Math.Min(_i + 1, steps.Length - 1); return steps[_i].Wait; }
        public int QueryStatus(out int status) { status = steps[_i].Status; return 0; }
    }

    private static readonly TimeSpan Long = TimeSpan.FromSeconds(10);

    [Fact]
    public void Pending_then_finished()
    {
        var op = new FakeAsync((0, VssAsyncWait.VSS_S_ASYNC_PENDING), (0, VssAsyncWait.VSS_S_ASYNC_PENDING), (0, VssAsyncWait.VSS_S_ASYNC_FINISHED));
        var trace = new List<string>();
        VssAsyncWait.Run(op, "DoSnapshotSet", Long, CancellationToken.None, trace, pollMs: 1);
        Assert.Equal(3, op.Polls);
        Assert.Contains(trace, t => t.Contains("(FINISHED)", StringComparison.Ordinal));
    }

    [Fact]
    public void S_ok_status_is_not_completion()
    {
        // hypothesis 1: an S_OK status must not end the wait while the operation is still running
        var op = new FakeAsync((0, 0), (0, 0), (0, VssAsyncWait.VSS_S_ASYNC_FINISHED));
        VssAsyncWait.Run(op, "DoSnapshotSet", Long, CancellationToken.None, [], pollMs: 1);
        Assert.Equal(3, op.Polls);
    }

    [Fact]
    public void Wait_timeout_codes_just_poll_again()
    {
        var op = new FakeAsync((VssAsyncWait.WAIT_TIMEOUT_HR, VssAsyncWait.VSS_S_ASYNC_PENDING), (VssAsyncWait.E_PENDING, VssAsyncWait.VSS_S_ASYNC_PENDING),
            (0x00000102, VssAsyncWait.VSS_S_ASYNC_PENDING), (0, VssAsyncWait.VSS_S_ASYNC_FINISHED));
        VssAsyncWait.Run(op, "PrepareForBackup", Long, CancellationToken.None, [], pollMs: 1);
        Assert.Equal(4, op.Polls);
    }

    [Fact]
    public void Deadline_cancels_the_operation()
    {
        var op = new FakeAsync((0, VssAsyncWait.VSS_S_ASYNC_PENDING));
        var trace = new List<string>();
        var ex = Assert.Throws<VssStepException>(() => VssAsyncWait.Run(op, "GatherWriterMetadata", TimeSpan.FromMilliseconds(50), CancellationToken.None, trace, pollMs: 1));
        Assert.StartsWith("GatherWriterMetadata did not finish", ex.Message, StringComparison.Ordinal); // the writer-less fallback keys on this prefix
        Assert.True(op.Cancelled);
        Assert.Same(trace, ex.Trace);
    }

    [Fact]
    public void Cancelled_status_and_caller_cancellation()
    {
        Assert.Throws<OperationCanceledException>(() =>
            VssAsyncWait.Run(new FakeAsync((0, VssAsyncWait.VSS_S_ASYNC_CANCELLED)), "x", Long, CancellationToken.None, [], pollMs: 1));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var op = new FakeAsync((0, VssAsyncWait.VSS_S_ASYNC_PENDING));
        Assert.ThrowsAny<OperationCanceledException>(() => VssAsyncWait.Run(op, "x", Long, cts.Token, [], pollMs: 1));
        Assert.True(op.Cancelled);
    }

    [Fact]
    public void Error_status_and_wait_failure_throw_with_code()
    {
        const int insufficientStorage = unchecked((int)0x8004231F);
        var ex = Assert.Throws<VssStepException>(() => VssAsyncWait.Run(new FakeAsync((0, VssAsyncWait.VSS_S_ASYNC_PENDING), (0, insufficientStorage)), "DoSnapshotSet", Long, CancellationToken.None, [], pollMs: 1));
        Assert.Equal(insufficientStorage, ex.Code);
        const int accessDenied = unchecked((int)0x80070005);
        var ex2 = Assert.Throws<VssStepException>(() => VssAsyncWait.Run(new FakeAsync((accessDenied, 0)), "DoSnapshotSet", Long, CancellationToken.None, [], pollMs: 1));
        Assert.Equal(accessDenied, ex2.Code);
    }
}
