using System.Globalization;
using System.Net;
using System.Text;
using BackupForge.Core.Config;
using BackupForge.Core.Execution;
using BackupForge.Core.History;
using BackupForge.Core.Resources;

namespace BackupForge.Core.Notifications;

/// <summary>Builds the immediate e-mails (SPEC 10.1). Everything passes the secret masker in the notifier.</summary>
public static class MailBuilder
{
    public const int MaxErrorsInMail = 20;
    public const long MaxAttachmentBytes = 1024 * 1024;

    public static string KindLabel(string kind, CultureInfo c) => kind switch
    {
        NotificationKinds.Failure => Texts.Get("mail.failure", c),
        NotificationKinds.Warning => Texts.Get("mail.warning", c),
        NotificationKinds.Recovery => Texts.Get("mail.recovery", c),
        NotificationKinds.Stale => Texts.Get("mail.stale", c),
        NotificationKinds.Config => Texts.Get("mail.config", c),
        _ => Texts.Get("mail.test", c),
    };

    public static string Subject(BackupConfig cfg, string kind, string what, string? summary)
    {
        var c = Texts.CultureFor(cfg.Language);
        var s = $"[BackupForge][{cfg.InstanceName}] {KindLabel(kind, c)}: {what}";
        if (!string.IsNullOrEmpty(summary)) s += " — " + summary;
        return s.Length > 250 ? s[..249] + "…" : s;
    }

    public static MailMessage ForRun(BackupConfig cfg, string kind, JobConfig job, RunRecord run, IReadOnlyList<RunItem> items, string? logPath, TimeZoneInfo zone)
    {
        var c = Texts.CultureFor(cfg.Language);
        var rows = new List<(string, string)>
        {
            (Texts.Get("mail.job", c), $"{job.Name} ({job.Id})"),
            (Texts.Get("mail.result", c), run.Result ?? ""),
            (Texts.Get("mail.trigger", c), run.Trigger),
            (Texts.Get("mail.start", c), TimeZoneInfo.ConvertTime(run.StartedAt, zone).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
            (Texts.Get("mail.duration", c), run.EndedAt is null ? "-" : Duration(run.EndedAt.Value - run.StartedAt)),
            (Texts.Get("mail.summary", c), run.Summary),
        };
        if (logPath is not null) rows.Add((Texts.Get("mail.log", c), logPath));
        var errors = items.Where(i => i.Level is ItemLevels.Error or ItemLevels.Warning).Take(MaxErrorsInMail)
            .Select(i => i.Path is null ? i.Message : $"{i.Path}: {i.Message}").ToList();
        var more = Math.Max(0, run.ErrorCount + run.WarningCount - errors.Count);
        var msg = Build(Subject(cfg, kind, job.Name, run.Summary), KindLabel(kind, c), kind, rows, errors, more, c);
        if (logPath is not null && File.Exists(logPath)) msg.Attachments.Add(LogAttachment(logPath, run.Id));
        return msg;
    }

    public static MailMessage Simple(BackupConfig cfg, string kind, string what, string text)
    {
        var c = Texts.CultureFor(cfg.Language);
        return Build(Subject(cfg, kind, what, null), KindLabel(kind, c), kind, [(Texts.Get("mail.summary", c), text)], [], 0, c);
    }

    private static MailMessage Build(string subject, string title, string kind, List<(string Key, string Value)> rows, List<string> errors, int more, CultureInfo c)
    {
        var color = kind switch
        {
            NotificationKinds.Failure or NotificationKinds.Stale or NotificationKinds.Config => "#b00020",
            NotificationKinds.Warning => "#b36b00",
            NotificationKinds.Recovery => "#1b7f3b",
            _ => "#33475b",
        };
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body style=\"font-family:Segoe UI,Arial,sans-serif;font-size:14px;color:#222\">");
        html.Append(CultureInfo.InvariantCulture, $"<h2 style=\"color:{color};margin:0 0 12px\">{H(title)}</h2>");
        html.Append("<table cellpadding=\"4\" style=\"border-collapse:collapse\">");
        foreach (var (k, v) in rows)
            html.Append(CultureInfo.InvariantCulture, $"<tr><td style=\"color:#666;vertical-align:top\">{H(k)}</td><td>{H(v)}</td></tr>");
        html.Append("</table>");
        var text = new StringBuilder();
        text.AppendLine(title).AppendLine();
        foreach (var (k, v) in rows) text.AppendLine(CultureInfo.InvariantCulture, $"{k}: {v}");
        if (errors.Count > 0)
        {
            html.Append(CultureInfo.InvariantCulture, $"<h3>{H(Texts.Get("mail.errors", c))}</h3><ul style=\"font-family:Consolas,monospace;font-size:12px\">");
            text.AppendLine().AppendLine(Texts.Get("mail.errors", c) + ":");
            foreach (var e in errors)
            {
                html.Append(CultureInfo.InvariantCulture, $"<li>{H(e)}</li>");
                text.AppendLine("  - " + e);
            }
            if (more > 0)
            {
                var m = Texts.Format(c, "mail.more_errors", more);
                html.Append(CultureInfo.InvariantCulture, $"<li>{H(m)}</li>");
                text.AppendLine("  " + m);
            }
            html.Append("</ul>");
        }
        html.Append("<p style=\"color:#888;font-size:11px\">BackupForge</p></body></html>");
        return new MailMessage { Subject = subject, Html = html.ToString(), Text = text.ToString() };
    }

    /// <summary>The run log if below 1 MB, else its last 200 lines.</summary>
    public static MailAttachment LogAttachment(string path, long runId)
    {
        var info = new FileInfo(path);
        byte[] content;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            if (info.Length < MaxAttachmentBytes)
            {
                content = new byte[fs.Length];
                fs.ReadExactly(content);
            }
            else
            {
                using var sr = new StreamReader(fs);
                var q = new Queue<string>();
                while (sr.ReadLine() is { } l) { q.Enqueue(l); if (q.Count > 200) q.Dequeue(); }
                content = Encoding.UTF8.GetBytes(string.Join("\r\n", q));
            }
        }
        return new MailAttachment($"run-{runId}.log", content);
    }

    public static string Duration(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours}:{d.Minutes:00}:{d.Seconds:00}" : $"{d.Minutes}:{d.Seconds:00}";

    private static string H(string s) => Html.Encode(s);
}
