using Freeboard.Core.Email;

namespace Freeboard.Email;

/// <summary>
/// A non-delivering developer sink for the <see cref="IEmailSender"/> seam. It logs the recipient
/// and subject at information level and returns, so wiring and local development can run with a
/// registered sender. It never emits the body, which carries the reset/magic-link token (a
/// credential), so it is not a working transport: an operator who needs real delivery must use the
/// SMTP transport.
/// </summary>
public sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Email not delivered (log transport): {Subject} to {Recipient}.", message.Subject, message.To);
        return Task.CompletedTask;
    }
}
