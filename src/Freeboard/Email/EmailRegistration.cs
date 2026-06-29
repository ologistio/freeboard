using Freeboard.Core.Email;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Freeboard.Email;

/// <summary>
/// Registers the <see cref="IEmailSender"/> selected by <c>Email:Transport</c> and fails fast on
/// an smtp transport that could not deliver. Binds one concrete <see cref="EmailOptions"/> instance
/// and registers the sender from it (the <c>AuthCryptoOptions</c> house pattern: a bound instance,
/// not <c>IOptions</c>). Kept as a seam so the switch and the smtp fail-fast are testable without a
/// web host.
/// </summary>
internal static class EmailRegistration
{
    /// <summary>
    /// Binds <c>Email</c>, registers the matching sender (none registers nothing, log the
    /// non-delivering dev sink, smtp the MailKit sender via a factory closing over the bound
    /// options), and throws on an unrecognised transport or an smtp transport missing what it needs
    /// to deliver. Returns the bound options.
    /// </summary>
    public static EmailOptions Add(IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(EmailOptions.SectionName).Get<EmailOptions>()
            ?? new EmailOptions();

        switch (options.Transport)
        {
            case EmailTransport.None:
                break;
            case EmailTransport.Log:
                services.AddSingleton<IEmailSender, LoggingEmailSender>();
                break;
            case EmailTransport.Smtp:
                services.AddSingleton<IEmailSender>(sp =>
                    new SmtpEmailSender(options, sp.GetRequiredService<ILogger<SmtpEmailSender>>()));
                break;
            default:
                throw new InvalidOperationException(
                    $"Email:Transport '{options.Transport}' is not a recognised transport (none, log, smtp).");
        }

        // Smtp fail-fast: validate only what is needed to deliver, so an smtp transport that could
        // not send never starts.
        if (options.Transport == EmailTransport.Smtp)
        {
            if (string.IsNullOrWhiteSpace(options.Smtp.Host))
            {
                throw new InvalidOperationException(
                    "Email:Transport is smtp but Email:Smtp:Host is missing.");
            }

            // FromName carries the display name, so FromAddress must be a bare addr-spec with a
            // domain: the sender passes it straight to MailboxAddress as the address, where a
            // display-name form ("Name <a@b>") would be a malformed address.
            if (string.IsNullOrWhiteSpace(options.FromAddress)
                || !MimeKit.MailboxAddress.TryParse(options.FromAddress, out var fromMailbox)
                || !HasLocalAndDomain(fromMailbox.Address)
                || !string.Equals(options.FromAddress.Trim(), fromMailbox.Address, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Email:Transport is smtp but Email:FromAddress is missing or not a bare email address.");
            }
        }

        return options;
    }

    // A deliverable addr-spec needs a non-empty local part and a non-empty domain. MimeKit parses a
    // bare token with no '@' as a mailbox, so require exactly one '@' with both sides present.
    private static bool HasLocalAndDomain(string addressSpec)
    {
        var at = addressSpec.IndexOf('@');
        return at > 0 && at < addressSpec.Length - 1 && addressSpec.IndexOf('@', at + 1) < 0;
    }
}
