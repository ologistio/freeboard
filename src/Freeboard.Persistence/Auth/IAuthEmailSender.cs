namespace Freeboard.Persistence.Auth;

/// <summary>
/// Outbound auth-email seam, one method per email kind. The concrete transport (SMTP, a
/// provider API) is operator config supplied later; this layer ships only the seam and
/// pulls in NO web dependency. If password reset is enabled with no registered sender, the
/// app fails fast at startup; magic-link is simply not offered without a sender.
/// </summary>
public interface IAuthEmailSender
{
    /// <summary>Sends a password-reset email carrying the one-time reset token to the user.</summary>
    Task SendPasswordResetAsync(string email, string resetToken, CancellationToken cancellationToken = default);

    /// <summary>Sends a magic-link MFA email carrying the single-use, short-TTL link token.</summary>
    Task SendMagicLinkAsync(string email, string magicLinkToken, CancellationToken cancellationToken = default);
}
