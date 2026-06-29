using Freeboard.Core.Email;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Freeboard.Email;

/// <summary>
/// An SMTP <see cref="IEmailSender"/> built on MailKit. It opens a fresh <see cref="SmtpClient"/>
/// per send: a MailKit client is not thread-safe and holds one connection, so a shared/pooled
/// client would serialise or corrupt concurrent sends. It takes the raw bound
/// <see cref="EmailOptions"/> (not <c>IOptions</c>) so the registration factory can pass the
/// instance it bound. The body may carry a token (a credential) and is never logged.
/// </summary>
public sealed class SmtpEmailSender(EmailOptions options, ILogger<SmtpEmailSender> logger)
    : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(options.FromName, options.FromAddress));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;

        // Text-only unless an HTML body is supplied; then send both as multipart/alternative.
        if (message.HtmlBody is null)
        {
            mime.Body = new TextPart("plain") { Text = message.TextBody };
        }
        else
        {
            mime.Body = new BodyBuilder { TextBody = message.TextBody, HtmlBody = message.HtmlBody }.ToMessageBody();
        }

        using var client = new SmtpClient { Timeout = options.Smtp.TimeoutSeconds * 1000 };
        var secureOptions = options.Smtp.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None;
        await client.ConnectAsync(options.Smtp.Host, options.Smtp.Port, secureOptions, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(options.Smtp.Username))
        {
            await client.AuthenticateAsync(options.Smtp.Username, options.Smtp.Password, cancellationToken).ConfigureAwait(false);
        }

        await client.SendAsync(mime, cancellationToken).ConfigureAwait(false);
        await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);

        // Never log the body; recipient + subject is enough to confirm a send was attempted.
        logger.LogInformation("Sent email {Subject} to {Recipient}.", message.Subject, message.To);
    }
}
