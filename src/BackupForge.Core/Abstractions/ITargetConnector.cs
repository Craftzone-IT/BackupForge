using BackupForge.Core.Config;

namespace BackupForge.Core.Abstractions;

/// <summary>
/// Makes a target reachable for the duration of a run (SMB with own credentials via WNetAddConnection2,
/// no drive letter — SPEC 14.). Local targets need nothing.
/// </summary>
public interface ITargetConnector
{
    /// <summary>Connects; the returned handle disconnects on dispose. Throws <see cref="TargetConnectException"/> with a readable message.</summary>
    IDisposable Connect(TargetConfig target, ISecretStore secrets);
}

public sealed class TargetConnectException(string message, int? errorCode = null) : Exception(message)
{
    public int? ErrorCode { get; } = errorCode;
}

public sealed class NoOpTargetConnector : ITargetConnector
{
    public static readonly NoOpTargetConnector Instance = new();
    public IDisposable Connect(TargetConfig target, ISecretStore secrets) => NullDisposable.Instance;
}

public sealed class NullDisposable : IDisposable
{
    public static readonly NullDisposable Instance = new();
    public void Dispose() { }
}

/// <summary>Volume Shadow Copy (SPEC 6.6). One session per run; disposing deletes the snapshots.</summary>
public interface ISnapshotProvider
{
    bool IsSupported { get; }
    /// <summary>Creates one snapshot per volume of the given paths (copy-only backup). Throws on failure.</summary>
    ISnapshotSession Create(IReadOnlyCollection<string> paths, CancellationToken ct);
    /// <summary>Deletes snapshots left over by a crashed service (by id). Returns the ids actually deleted.</summary>
    IReadOnlyList<string> DeleteOrphans(IEnumerable<string> snapshotIds);
}

public interface ISnapshotSession : IDisposable
{
    /// <summary>Translates an original path to the same path inside the snapshot.</summary>
    string Translate(string path);
    IReadOnlyList<(string SnapshotId, string? SetId, string Volume)> Snapshots { get; }
}

public sealed class NoSnapshotProvider : ISnapshotProvider
{
    public static readonly NoSnapshotProvider Instance = new();
    public bool IsSupported => false;
    public ISnapshotSession Create(IReadOnlyCollection<string> paths, CancellationToken ct) => throw new NotSupportedException("VSS is not available on this system");
    public IReadOnlyList<string> DeleteOrphans(IEnumerable<string> snapshotIds) => [];
}
