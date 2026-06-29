namespace Freeboard.Core.Email;

/// <summary>
/// Provider-level outbound email seam. A concrete transport (SMTP, a provider API) implements
/// this single method; the message it is handed is already built by the caller. The seam lives in
/// Core so any component can build an <see cref="EmailMessage"/>, while transports stay in the
/// component that owns operator config. Bodies carry credentials (reset/magic-link tokens) and
/// must never be logged.
/// </summary>
public interface IEmailSender
{
    /// <summary>Delivers <paramref name="message"/> via the configured transport.</summary>
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}
