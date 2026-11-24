using BackupForge.Core.Config;

namespace BackupForge.Core.Notifications;

public sealed record MailAttachment(string FileName, byte[] Content, string MediaType = "text/plain");

public sealed class MailMessage
{
    public required string Subject { get; init; }
    public required string Html { get; init; }
    public required string Text { get; init; }
    public List<MailAttachment> Attachments { get; } = [];
}

/// <summary>Sends one e-mail with the configured SMTP settings (MailKit in the service, fakes in tests).</summary>
public interface IMailSender
{
    Task SendAsync(EmailSettings settings, string? password, MailMessage message, CancellationToken ct);
}

public static class NotificationKinds
{
    public const string Failure = "failure";
    public const string Warning = "warning";
    public const string Recovery = "recovery";
    public const string Stale = "stale";
    public const string Config = "config";
    public const string Test = "test";
    public const string WeeklyReport = "weekly_report";
    public const string Heartbeat = "heartbeat";
}

/// <summary>Windows event ids (docs/EVENT-IDS.md).</summary>
public static class EventIds
{
    public const int ServiceStarted = 1000;
    public const int ServiceStopped = 1001;
    public const int JobWarning = 2001;
    public const int JobFailed = 2002;
    public const int DeleteGuard = 2003;
    public const int JobStale = 2004;
    public const int VssCleanup = 2005;
    public const int ConfigError = 2010;
    public const int SmtpFailed = 2020;
    public const int HeartbeatFailed = 2021;
    public const int UnexpectedError = 2090;
}

public enum EventLevel { Information, Warning, Error }

/// <summary>Windows event log abstraction (no-op elsewhere).</summary>
public interface IEventSink
{
    void Write(EventLevel level, int eventId, string message);
}

public sealed class NullEventSink : IEventSink
{
    public static readonly NullEventSink Instance = new();
    public void Write(EventLevel level, int eventId, string message) { }
}

public sealed class MemoryEventSink : IEventSink
{
    public List<(EventLevel Level, int Id, string Message)> Events { get; } = [];
    public void Write(EventLevel level, int eventId, string message) { lock (Events) Events.Add((level, eventId, message)); }
}
