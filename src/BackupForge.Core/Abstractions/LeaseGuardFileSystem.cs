namespace BackupForge.Core.Abstractions;

/// <summary>
/// CR-003 2.: an <see cref="IFileSystem"/> decorator that catches UNC access to a credentialed SMB server without a live
/// lease (the LocalSystem would then connect with the computer account → error 1219 for the next run). A violation throws
/// in tests / debug builds (<see cref="ThrowOnViolation"/>) and logs one warning per server per hour in production.
/// Local paths and UNC servers without own credentials (the service account's access is intended there) are never checked.
/// </summary>
public sealed class LeaseGuardFileSystem(IFileSystem inner) : IFileSystem
{
    private readonly Dictionary<string, DateTime> _lastWarned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Does this server (lower case) need a lease, i.e. is it a target with its own user name?</summary>
    public Func<string, bool> NeedsLease { get; set; } = _ => false;
    /// <summary>Is there a live lease for this server right now (the SMB connection manager)?</summary>
    public Func<string, bool> HasLease { get; set; } = _ => false;
    public bool ThrowOnViolation { get; set; }
    public Action<string>? Warn { get; set; }
    public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;
    public int Violations { get; private set; }

    public static string? ServerOf(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)) return null;
        if (!path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith("//", StringComparison.Ordinal)) return null;
        var rest = path[2..];
        var end = rest.IndexOfAny(['\\', '/']);
        var server = end < 0 ? rest : rest[..end];
        return server.Length == 0 ? null : server.ToLowerInvariant();
    }

    private string Check(string path)
    {
        if (ServerOf(path) is not { } server || !NeedsLease(server) || HasLease(server)) return path;
        lock (_lastWarned)
        {
            Violations++;
            var message = $"SMB access to {server} without a connection lease ({path}) — this would connect with the computer account (error 1219)";
            if (ThrowOnViolation) throw new InvalidOperationException(message);
            var now = UtcNow();
            if (!_lastWarned.TryGetValue(server, out var last) || now - last >= TimeSpan.FromHours(1))
            {
                _lastWarned[server] = now;
                Warn?.Invoke(message);
            }
        }
        return path;
    }

    public char Separator => inner.Separator;
    public bool IsCaseSensitive => inner.IsCaseSensitive;
    public bool FileExists(string path) => inner.FileExists(Check(path));
    public bool DirectoryExists(string path) => inner.DirectoryExists(Check(path));
    public FsEntry? GetEntry(string path) => inner.GetEntry(Check(path));
    public IEnumerable<FsEntry> Enumerate(string directory) => inner.Enumerate(Check(directory));
    public void CreateDirectory(string path) => inner.CreateDirectory(Check(path));
    public void DeleteFile(string path) => inner.DeleteFile(Check(path));
    public void DeleteDirectory(string path, bool recursive) => inner.DeleteDirectory(Check(path), recursive);
    public void MoveFile(string source, string destination, bool overwrite) => inner.MoveFile(Check(source), Check(destination), overwrite);
    public void MoveDirectory(string source, string destination) => inner.MoveDirectory(Check(source), Check(destination));
    public Stream OpenRead(string path) => inner.OpenRead(Check(path));
    public Stream Create(string path) => inner.Create(Check(path));
    public void Flush(Stream stream) => inner.Flush(stream);
    public void SetTimes(string path, DateTime creationUtc, DateTime lastWriteUtc) => inner.SetTimes(Check(path), creationUtc, lastWriteUtc);
    public void SetAttributes(string path, FileAttributes attributes) => inner.SetAttributes(Check(path), attributes);
    public (long Free, long Total)? GetSpace(string path) => inner.GetSpace(Check(path));
}
