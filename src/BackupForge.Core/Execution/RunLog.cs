using System.Globalization;
using System.Text;
using BackupForge.Core.Abstractions;
using BackupForge.Core.Logging;

namespace BackupForge.Core.Execution;

/// <summary>Detailed per-run log (logs\jobs\&lt;jobId&gt;\&lt;runId&gt;.log).</summary>
public interface IRunLog
{
    void Write(string level, string message);
}

public static class RunLogExtensions
{
    public static void Info(this IRunLog log, string message) => log.Write("INF", message);
    public static void Warning(this IRunLog log, string message) => log.Write("WRN", message);
    public static void Error(this IRunLog log, string message) => log.Write("ERR", message);
}

public sealed class FileRunLog : IRunLog, IDisposable
{
    private readonly StreamWriter _writer;
    private readonly IClock _clock;
    private readonly SecretMasker _masker;
    private readonly Lock _lock = new();

    public FileRunLog(string path, IClock clock, SecretMasker masker)
    {
        Path = path;
        _clock = clock;
        _masker = masker;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false));
    }

    public string Path { get; }

    public void Write(string level, string message)
    {
        var line = $"{_clock.LocalNow().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} {level} {_masker.Apply(message)}";
        lock (_lock)
        {
            try
            {
                _writer.WriteLine(line);
                _writer.Flush();
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }

    public void Dispose()
    {
        lock (_lock) _writer.Dispose();
    }
}

/// <summary>Collects lines in memory (tests, dry-run output).</summary>
public sealed class MemoryRunLog : IRunLog
{
    private readonly List<string> _lines = [];
    public IReadOnlyList<string> Lines { get { lock (_lines) return [.. _lines]; } }
    public void Write(string level, string message) { lock (_lines) _lines.Add($"{level} {message}"); }
}

public static class RunLogRetention
{
    /// <summary>Deletes run logs (only *.log named by run id) older than the retention, and empty job folders.</summary>
    public static int Cleanup(DataPaths paths, int retentionDays, DateTimeOffset nowUtc)
    {
        if (!Directory.Exists(paths.JobLogsDir)) return 0;
        var cutoff = nowUtc.UtcDateTime.AddDays(-retentionDays);
        var deleted = 0;
        foreach (var dir in Directory.EnumerateDirectories(paths.JobLogsDir))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.log"))
            {
                if (!long.TryParse(System.IO.Path.GetFileNameWithoutExtension(file), NumberStyles.None, CultureInfo.InvariantCulture, out _)) continue;
                if (File.GetLastWriteTimeUtc(file) >= cutoff) continue;
                try { File.Delete(file); deleted++; } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return deleted;
    }
}
