namespace BackupForge.Core.Abstractions;

/// <summary>One directory entry as seen by the engines.</summary>
public sealed record FsEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    long Length,
    DateTime LastWriteUtc,
    DateTime CreationUtc,
    FileAttributes Attributes)
{
    public bool IsReparsePoint => (Attributes & FileAttributes.ReparsePoint) != 0;

    /// <summary>OneDrive Files On-Demand placeholder (RECALL_ON_DATA_ACCESS 0x400000 / RECALL_ON_OPEN 0x40000 / OFFLINE).</summary>
    public bool IsCloudOnly => ((int)Attributes & (0x400000 | 0x40000)) != 0 || (Attributes & FileAttributes.Offline) != 0;
}

/// <summary>
/// File system abstraction used by the engines. Paths are plain .NET paths; on Windows .NET itself
/// switches to extended-length (\\?\) paths when needed, so callers never have to prefix them.
/// </summary>
public interface IFileSystem
{
    char Separator { get; }
    bool IsCaseSensitive { get; }

    bool FileExists(string path);
    bool DirectoryExists(string path);
    FsEntry? GetEntry(string path);
    IEnumerable<FsEntry> Enumerate(string directory);

    void CreateDirectory(string path);
    void DeleteFile(string path);
    /// <summary>Deletes an (empty unless recursive) directory.</summary>
    void DeleteDirectory(string path, bool recursive);
    void MoveFile(string source, string destination, bool overwrite);
    void MoveDirectory(string source, string destination);

    Stream OpenRead(string path);
    Stream Create(string path);
    void Flush(Stream stream);

    void SetTimes(string path, DateTime creationUtc, DateTime lastWriteUtc);
    void SetAttributes(string path, FileAttributes attributes);

    /// <summary>Free and total bytes of the volume that holds <paramref name="path"/>; null if unknown.</summary>
    (long Free, long Total)? GetSpace(string path);
}

public sealed class PhysicalFileSystem : IFileSystem
{
    public static readonly PhysicalFileSystem Instance = new();

    public char Separator => Path.DirectorySeparatorChar;
    public bool IsCaseSensitive => !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS();

    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public FsEntry? GetEntry(string path)
    {
        var fi = new FileInfo(path);
        if (fi.Exists) return ToEntry(fi);
        var di = new DirectoryInfo(path);
        return di.Exists ? ToEntry(di) : null;
    }

    public IEnumerable<FsEntry> Enumerate(string directory)
    {
        var di = new DirectoryInfo(directory);
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };
        foreach (var info in di.EnumerateFileSystemInfos("*", opts))
            yield return info is DirectoryInfo d ? ToEntry(d) : ToEntry((FileInfo)info);
    }

    private static FsEntry ToEntry(FileInfo f) =>
        new(f.Name, f.FullName, false, f.Length, f.LastWriteTimeUtc, f.CreationTimeUtc, f.Attributes);

    private static FsEntry ToEntry(DirectoryInfo d) =>
        new(d.Name, d.FullName, true, 0, d.LastWriteTimeUtc, d.CreationTimeUtc, d.Attributes);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public void DeleteFile(string path)
    {
        var attrs = File.GetAttributes(path);
        if ((attrs & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        File.Delete(path);
    }

    public void DeleteDirectory(string path, bool recursive)
    {
        if (recursive)
        {
            // Clear read-only flags first, otherwise Directory.Delete fails on Windows.
            foreach (var f in Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
            {
                var a = File.GetAttributes(f);
                if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(f, a & ~FileAttributes.ReadOnly);
            }
        }
        Directory.Delete(path, recursive);
    }

    public void MoveFile(string source, string destination, bool overwrite) => File.Move(source, destination, overwrite);
    public void MoveDirectory(string source, string destination) => Directory.Move(source, destination);

    public Stream OpenRead(string path) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);

    public Stream Create(string path) =>
        new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.SequentialScan);

    public void Flush(Stream stream)
    {
        if (stream is FileStream fs) fs.Flush(flushToDisk: true);
        else stream.Flush();
    }

    public void SetTimes(string path, DateTime creationUtc, DateTime lastWriteUtc)
    {
        File.SetCreationTimeUtc(path, creationUtc);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
    }

    public void SetAttributes(string path, FileAttributes attributes) => File.SetAttributes(path, attributes);

    public (long Free, long Total)? GetSpace(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows() && path.StartsWith(@"\\", StringComparison.Ordinal))
                return WindowsDiskSpace.Get(path);
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return null;
            var best = DriveInfo.GetDrives()
                .Where(d => d.IsReady && Path.GetFullPath(path).StartsWith(d.RootDirectory.FullName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => d.RootDirectory.FullName.Length)
                .FirstOrDefault();
            return best is null ? null : (best.AvailableFreeSpace, best.TotalSize);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}

internal static partial class WindowsDiskSpace
{
    [System.Runtime.InteropServices.LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf16, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceEx(string directory, out ulong freeForCaller, out ulong total, out ulong totalFree);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static (long, long)? Get(string path)
    {
        var dir = path.EndsWith('\\') ? path : path + "\\";
        return GetDiskFreeSpaceEx(dir, out var free, out var total, out _) ? ((long)free, (long)total) : null;
    }
}
