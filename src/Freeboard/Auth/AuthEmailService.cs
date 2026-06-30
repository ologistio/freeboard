using Freeboard.Core.Email;

namespace Freeboard.Auth;

/// <summary>
/// Builds the auth emails (password reset, magic link) and delegates delivery to the generic
/// <see cref="IEmailSender"/>. It owns the auth-link concern - the absolute base URL the links are
/// built from - which is not part of the generic email config. Subjects and bodies are plain text.
/// The token is a credential and is never logged.
/// </summary>
public sealed class AuthEmailService
{
    private readonly IEmailSender _sender;
    private readonly string _baseUrl;

    /// <summary>
    /// Validates the auth base URL eagerly: it must be an absolute http(s) URL with a host, or the
    /// reset/magic-link bodies would carry unreachable links. Registered only when an
    /// <see cref="IEmailSender"/> is present, so an invalid base URL fails fast at startup.
    /// </summary>
    public AuthEmailService(IEmailSender sender, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)
            || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(baseUri.Host))
        {
            throw new InvalidOperationException(
                "Auth:Email:BaseUrl is missing or not an absolute http(s) URL.");
        }

        _sender = sender;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    public Task SendPasswordResetAsync(string email, string resetToken, CancellationToken cancellationToken = default)
    {
        var url = BuildLink("/reset-password", resetToken);
        var body =
            $"A password reset was requested for your account. Use the link below to set a new password:\n\n{url}\n\nIf you did not request this, you can ignore this email.";
        return _sender.SendAsync(
            new EmailMessage(email, "Reset your Freeboard password", body), cancellationToken);
    }

    public Task SendMagicLinkAsync(string email, string magicLinkToken, CancellationToken cancellationToken = default)
    {
        var url = BuildLink("/auth/magic-link", magicLinkToken);
        var body =
            $"Use the link below to continue signing in:\n\n{url}\n\nThis link is single-use and expires shortly.";
        return _sender.SendAsync(
            new EmailMessage(email, "Your Freeboard sign-in link", body), cancellationToken);
    }

    public Task SendInviteAsync(string email, string inviteToken, CancellationToken cancellationToken = default)
    {
        // The invite reuses the password-reset link/page: the recipient sets their own password. The
        // token is single-use and expiry-bounded by the reset store; it is never logged.
        var url = BuildLink("/reset-password", inviteToken);
        var body =
            $"You have been invited to Freeboard. Use the link below to set your password and sign in:\n\n{url}\n\nThis link is single-use and expires in 7 days.";
        return _sender.SendAsync(
            new EmailMessage(email, "Your Freeboard invitation", body), cancellationToken);
    }

    private string BuildLink(string path, string token)
        => $"{_baseUrl}{path}?token={Uri.EscapeDataString(token)}";
}
