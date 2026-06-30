using Freeboard.Auth;
using Freeboard.Persistence.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Freeboard.Pages.Account.Mfa;

/// <summary>
/// Removes the authenticator (TOTP) factor. POST-only, under <c>/account</c> (auth required) and
/// sudo-gated like the other MFA-management actions: it checks sudo recency with the shared predicate
/// and redirects to <c>/account/sudo</c> when the step-up is stale, before performing the delete. A
/// plain antiforgery form drives it; no JS is needed.
/// </summary>
public sealed class TotpRemoveModel(
    ITotpStore totp,
    IWebAuthnCredentialStore webAuthn,
    IUserStore users,
    ISessionStore sessions,
    IOptions<WebAuthOptions> webAuthOptions) : PageModel
{
    private readonly WebAuthOptions _webAuth = webAuthOptions.Value;

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var sessionId = User.FindFirst(AuthClaims.SessionId)?.Value;
        if (!await SudoRecency.IsRecentAsync(sessions, sessionId, _webAuth.SudoModeTtl).ConfigureAwait(false))
        {
            return Redirect("/account/sudo?returnUrl=/account/mfa");
        }

        await AuthFlows.TotpDeleteAsync(User.FindFirst(AuthClaims.UserId)?.Value, totp, webAuthn, users, ct)
            .ConfigureAwait(false);
        return Redirect("/account/mfa");
    }

    public IActionResult OnGet() => Redirect("/account/mfa");
}
