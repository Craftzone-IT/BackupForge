using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using BackupForge.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace BackupForge.Core.Logging;

/// <summary>
/// Daily rotated text log (<c>&lt;prefix&gt;-yyyyMMdd.log</c>), retention in days; every line passes the secret masker.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly string _prefix;
    private readonly int _retentionDays;
    private readonly IClock _clock;
    private readonly SecretMasker _masker;
    private readonly LogLevel _minLevel;
    private readonly Lock _lock = new();
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private StreamWriter? _writer;
    private DateOnly _currentDay;

    public FileLoggerProvider(string directory, string prefix, int retentionDays, IClock clock, SecretMasker masker, LogLevel minLevel = LogLevel.Information)
    {
        _directory = directory;
        _prefix = prefix;
        _retentionDays = retentionDays;
        _clock = clock;
        _masker = masker;
        _minLevel = minLevel;
    }

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, n => new FileLogger(this, n));

    internal bool IsEnabled(LogLevel level) => level >= _minLevel && level != LogLevel.None;

    internal void Write(LogLevel level, string category, string message, Exception? ex)
    {
        var now = _clock.LocalNow();
        var sb = new StringBuilder(message.Length + 64);
        sb.Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(' ')
          .Append(LevelText(level)).Append(' ')
          .Append(ShortCategory(category)).Append(": ")
          .Append(message);
        if (ex is not null) sb.AppendLine().Append(ex);
        var line = _masker.Apply(sb.ToString());

        lock (_lock)
        {
            try
            {
                var day = DateOnly.FromDateTime(now.DateTime);
                if (_writer is null || day != _currentDay) Rotate(day);
                _writer!.WriteLine(line);
                _writer.Flush();
            }
            catch (IOException)
            {
                // Logging must never take the service down.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void Rotate(DateOnly day)
    {
        _writer?.Dispose();
        Directory.CreateDirectory(_directory);
        _currentDay = day;
        var path = Path.Combine(_directory, $"{_prefix}-{day:yyyyMMdd}.log");
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)); // BOM only at position 0 = a new file (PS 5.1 Get-Content, CR-002 A7.)
        CleanupOld(day);
    }

    private void CleanupOld(DateOnly today)
    {
        var cutoff = today.AddDays(-_retentionDays);
        foreach (var file in Directory.EnumerateFiles(_directory, $"{_prefix}-*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var datePart = name[(_prefix.Length + 1)..];
            if (datePart.Length == 8 &&
                DateOnly.TryParseExact(datePart, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) &&
                d < cutoff)
            {
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static string LevelText(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };

    private static string ShortCategory(string category)
    {
        var i = category.LastIndexOf('.');
        return i >= 0 ? category[(i + 1)..] : category;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            provider.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}
