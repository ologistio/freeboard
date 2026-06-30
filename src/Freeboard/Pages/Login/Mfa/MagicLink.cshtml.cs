using Freeboard.Auth;
using Freeboard.Persistence.Auth;
using Microsoft.AspNetCore.Mvc;
using Freeboard.Web;
using Microsoft.Extensions.Options;

namespace Freeboard.Pages.Login.Mfa;

/// <summary>
/// Login-MFA magic-link send. Anonymous (the user is mid-login). The GET renders the "send me a link"
/// form when a pending challenge offers magic-link, else a restart message. The POST drives the
/// shared send flow with the held <c>mfa_token</c>, which emails the
/// <c>{baseUrl}/auth/magic-link?token=</c> link, then renders a uniform "check your email"
/// confirmation regardless of the send outcome so nothing about the account or factor is revealed.
/// The actual verify happens later on the landing page when the emailed link is opened.
/// </summary>
public sealed class MagicLinkModel(
    PendingMfaStore pendingMfa,
    MfaChallengeService mfa,
    IMfaChallengeStore challenges,
    ITokenHasher tokenHasher,
    IUserStore users,
    AuthRateLimiter rateLimiter,
    IServiceProvider serviceProvider,
    IOptions<WebAuthOptions> webAuthOptions)
    : MfaChallengePageModel(pendingMfa, mfa, webAuthOptions)
{
    private readonly MfaChallengeService _mfa = mfa;
    private readonly IUserStore _users = users;
    private readonly AuthRateLimiter _rateLimiter = rateLimiter;

    /// <summary>True after a send attempt, so the page shows the uniform confirmation.</summary>
    public bool Sent { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
        => await LoadPendingAsync(MfaFactors.MagicLink, ct).ConfigureAwait(false);

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!await LoadPendingAsync(MfaFactors.MagicLink, ct).ConfigureAwait(false))
        {
            return Page();
        }

        // The outcome is intentionally not surfaced: sent, rate-limited, send-cap, and even a transient
        // send failure (for example an SMTP outage) all render the same confirmation so the page is
        // neither an oracle nor a 500. The challenge stays pending for the landing, matching the
        // forgot-password hardening.
        try
        {
            await AuthFlows.MagicLinkSendAsync(
                MfaToken, HttpContext.Connection.RemoteIpAddress?.ToString(),
                _mfa, challenges, tokenHasher, _users, _rateLimiter, serviceProvider, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed send must not 500 the screen or block login; the sender logs its own failures.
        }

        Sent = true;
        return Page();
    }
}
