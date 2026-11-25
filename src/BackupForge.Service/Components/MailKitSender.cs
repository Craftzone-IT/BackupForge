using BackupForge.Core.Config;
using BackupForge.Core.Notifications;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace BackupForge.Service.Components;

public sealed class MailKitSender : IMailSender
{
    public async Task SendAsync(EmailSettings s, string? password, Core.Notifications.MailMessage message, CancellationToken ct)
    {
        var mime = new MimeMessage();
        mime.From.Add(MailboxAddress.Parse(s.From));
        foreach (var to in s.To) mime.To.Add(MailboxAddress.Parse(to));
        mime.Subject = message.Subject;
        var body = new BodyBuilder { HtmlBody = message.Html, TextBody = message.Text };
        foreach (var a in message.Attachments) body.Attachments.Add(a.FileName, a.Content, ContentType.Parse(a.MediaType + "; charset=utf-8"));
        mime.Body = body.ToMessageBody();

        using var client = new SmtpClient { Timeout = 60_000 };
        var security = s.Security switch
        {
            SmtpSecurityModes.None => SecureSocketOptions.None,
            SmtpSecurityModes.SslOnConnect => SecureSocketOptions.SslOnConnect,
            _ => SecureSocketOptions.StartTls,
        };
        await client.ConnectAsync(s.SmtpHost, s.SmtpPort, security, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(s.Username)) await client.AuthenticateAsync(s.Username, password ?? "", ct).ConfigureAwait(false);
        await client.SendAsync(mime, ct).ConfigureAwait(false);
        await client.DisconnectAsync(true, ct).ConfigureAwait(false);
    }
}
