using BackupForge.Core.Abstractions;

namespace BackupForge.Engine.Files;

/// <summary>
/// Copies one file safely (SPEC 6.3): &lt;dest&gt;.bfpart → flush → (old version moved away) → rename → times + attributes.
/// </summary>
public sealed class FileCopier
{
    public const string PartSuffix = ".bfpart";
    private const FileAttributes Preserved = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive;

    private readonly IFileSystem _fs;
    private readonly Throttle? _throttle;

    public FileCopier(IFileSystem fs, Throttle? throttle)
    {
        _fs = fs;
        _throttle = throttle;
    }

    /// <summary>Copies <paramref name="source"/> to <paramref name="dest"/>. <paramref name="moveAway"/> receives an existing destination (to version it) — if null, it is deleted.</summary>
    public long Copy(FsEntry source, string dest, Action<string>? moveAway, Action<long>? progress, CancellationToken ct)
    {
        var part = dest + PartSuffix;
        long copied = 0;
        try
        {
            using (var input = _fs.OpenRead(source.FullPath))
            using (var output = _fs.Create(part))
            {
                var buffer = new byte[1 << 20];
                int n;
                while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    _throttle?.Wait(n, ct);
                    output.Write(buffer, 0, n);
                    copied += n;
                    progress?.Invoke(n);
                }
                _fs.Flush(output);
            }

            var existing = _fs.GetEntry(dest);
            if (existing is not null && !existing.IsDirectory)
            {
                if ((existing.Attributes & FileAttributes.ReadOnly) != 0) _fs.SetAttributes(dest, existing.Attributes & ~FileAttributes.ReadOnly);
                if (moveAway is not null) moveAway(dest);
                else _fs.DeleteFile(dest);
            }
            _fs.MoveFile(part, dest, overwrite: true);
            ApplyMetadata(source, dest);
            return copied;
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

    public void ApplyMetadata(FsEntry source, string dest)
    {
        try { _fs.SetTimes(dest, source.CreationUtc, source.LastWriteUtc); }
        catch (Exception ex) when (ex is PlatformNotSupportedException or IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            // creation time is not settable everywhere (e.g. some NAS / Linux); the write time matters
            try { File.SetLastWriteTimeUtc(dest, source.LastWriteUtc); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        var attrs = source.Attributes & Preserved;
        if (attrs == 0) attrs = FileAttributes.Normal;
        try { _fs.SetAttributes(dest, attrs); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
    }

    private void TryDelete(string path)
    {
        try { if (_fs.FileExists(path)) _fs.DeleteFile(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Simple token bucket shared by all copy threads of a run (SPEC 6.3 throttle_mbps).</summary>
public sealed class Throttle(double megabitsPerSecond)
{
    private readonly double _bytesPerSecond = megabitsPerSecond * 1_000_000 / 8;
    private readonly Lock _lock = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private double _available;
    private double _last;

    public void Wait(int bytes, CancellationToken ct)
    {
        while (true)
        {
            TimeSpan sleep;
            lock (_lock)
            {
                var now = _clock.Elapsed.TotalSeconds;
                _available = Math.Min(_bytesPerSecond, _available + (now - _last) * _bytesPerSecond);
                _last = now;
                if (_available >= bytes || _available >= _bytesPerSecond) { _available -= bytes; return; }
                sleep = TimeSpan.FromSeconds((bytes - _available) / _bytesPerSecond);
            }
            ct.WaitHandle.WaitOne(sleep < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : sleep);
            ct.ThrowIfCancellationRequested();
        }
    }
}
