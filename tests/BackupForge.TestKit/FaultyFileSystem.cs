using BackupForge.Core.Abstractions;

namespace BackupForge.TestKit;

/// <summary>Wraps the physical file system and injects failures for chosen paths.</summary>
public sealed class FaultyFileSystem(IFileSystem inner) : IFileSystem
{
    private readonly Dictionary<string, int> _openFailures = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>OpenRead(path) throws IOException this many times (int.MaxValue = always).</summary>
    public void FailOpen(string path, int times) { lock (_lock) _openFailures[path] = times; }

    /// <summary>After this many successful Create() calls, the given directory "disappears" (network loss).</summary>
    public int? LoseDirectoryAfterCreates { get; set; }
    public string? LostDirectory { get; set; }
    private int _creates;

    public int OpenAttempts { get; private set; }

    private bool Lost => LostDirectory is not null && LoseDirectoryAfterCreates is { } n && _creates >= n;

    public char Separator => inner.Separator;
    public bool IsCaseSensitive => inner.IsCaseSensitive;
    public bool FileExists(string path) => inner.FileExists(path);
    public bool DirectoryExists(string path) => !(Lost && path.StartsWith(LostDirectory!, StringComparison.Ordinal)) && inner.DirectoryExists(path);
    public FsEntry? GetEntry(string path) => inner.GetEntry(path);
    public IEnumerable<FsEntry> Enumerate(string directory) => inner.Enumerate(directory);
    public void CreateDirectory(string path) => inner.CreateDirectory(path);
    public void DeleteFile(string path) => inner.DeleteFile(path);
    public void DeleteDirectory(string path, bool recursive) => inner.DeleteDirectory(path, recursive);
    public void MoveFile(string source, string destination, bool overwrite) => inner.MoveFile(source, destination, overwrite);
    public void MoveDirectory(string source, string destination) => inner.MoveDirectory(source, destination);

    public Stream OpenRead(string path)
    {
        lock (_lock)
        {
            OpenAttempts++;
            if (_openFailures.TryGetValue(path, out var n) && n > 0)
            {
                if (n != int.MaxValue) _openFailures[path] = n - 1;
                throw new IOException($"The process cannot access the file '{path}' because it is being used by another process.");
            }
        }
        return inner.OpenRead(path);
    }

    public Stream Create(string path)
    {
        lock (_lock)
        {
            if (Lost && path.StartsWith(LostDirectory!, StringComparison.Ordinal)) throw new IOException("The network path was not found.");
            _creates++;
        }
        return inner.Create(path);
    }

    public void Flush(Stream stream) => inner.Flush(stream);
    public void SetTimes(string path, DateTime creationUtc, DateTime lastWriteUtc) => inner.SetTimes(path, creationUtc, lastWriteUtc);
    public void SetAttributes(string path, FileAttributes attributes) => inner.SetAttributes(path, attributes);
    public (long Free, long Total)? GetSpace(string path) => inner.GetSpace(path);
}
