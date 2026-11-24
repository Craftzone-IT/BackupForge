using System.Diagnostics;

namespace BackupForge.Windows;

/// <summary>Windows Application event log, source "BackupForge" (SPEC 9.2; ids: docs/EVENT-IDS.md).</summary>
public sealed class EventLogSink
{
    public const string Source = "BackupForge";
    public const string LogName = "Application";
    private readonly bool _available;

    public EventLogSink()
    {
        try
        {
            if (!EventLog.SourceExists(Source)) EventLog.CreateEventSource(Source, LogName);
            _available = true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            _available = false; // needs admin once (the installer registers the source)
        }
    }

    public bool Available => _available;

    public void Write(EventLogEntryType type, int eventId, string message)
    {
        if (!_available) return;
        try
        {
            EventLog.WriteEntry(Source, message.Length > 31000 ? message[..31000] : message, type, eventId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException) { }
    }
}

/// <summary>Adapter to the platform-independent sink.</summary>
public sealed class WindowsEventSink : BackupForge.Core.Notifications.IEventSink
{
    private readonly EventLogSink _sink = new();

    public void Write(BackupForge.Core.Notifications.EventLevel level, int eventId, string message) =>
        _sink.Write(level switch
        {
            BackupForge.Core.Notifications.EventLevel.Error => EventLogEntryType.Error,
            BackupForge.Core.Notifications.EventLevel.Warning => EventLogEntryType.Warning,
            _ => EventLogEntryType.Information,
        }, eventId, message);
}
