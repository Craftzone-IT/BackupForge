using BackupForge.Core.Config;
using BackupForge.Core.Notifications;
using BackupForge.Service.Components;
using BackupForge.TestKit;

namespace BackupForge.Service.Tests;

public class MailKitSenderTests
{
    [Fact]
    public async Task Sends_html_text_and_attachment_over_smtp()
    {
        await using var sink = new SmtpSink();
        var settings = new EmailSettings
        {
            Enabled = true, SmtpHost = "127.0.0.1", SmtpPort = sink.Port, Security = SmtpSecurityModes.None,
            From = "bf@example.com", To = ["it@example.com", "boss@example.com"], Username = "bf@example.com",
        };
        var msg = new MailMessage { Subject = "[BackupForge][SRV01] HIBA: Céges adatok", Html = "<b>hiba</b>", Text = "hiba" };
        msg.Attachments.Add(new MailAttachment("run-1.log", "log line"u8.ToArray()));
        await new MailKitSender().SendAsync(settings, "test-password-123", msg, CancellationToken.None);
        var raw = Assert.Single(sink.Messages);
        Assert.Contains("multipart", raw, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("run-1.log", raw, StringComparison.Ordinal);
        Assert.Contains("it@example.com", raw, StringComparison.Ordinal);
        Assert.Single(sink.AuthLines);
    }
}
