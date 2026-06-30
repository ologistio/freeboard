using Freeboard.Auth;
using Freeboard.Persistence.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Freeboard.Pages.Account.Mfa;

/// <summary>
/// Removes a registered passkey. POST-only, under <c>/account</c> (auth required) and sudo-gated like
/// registration: it checks sudo recency with the shared predicate and redirects to <c>/account/sudo</c>
/// when the step-up is stale. A plain antiforgery form carries the credential id; no JS is needed.
/// </summary>
public sealed class PasskeyRemoveModel(
    IWebAuthnCredentialStore creds,
    ITotpStore totp,
    IUserStore users,
    ISessionStore sessions,
    IOptions<WebAuthOptions> webAuthOptions) : PageModel
{
    private readonly WebAuthOptions _webAuth = webAuthOptions.Value;

    public async Task<IActionResult> OnPostAsync(string? id, CancellationToken ct)
    {
        var userId = User.FindFirst(AuthClaims.UserId)?.Value;
        var sessionId = User.FindFirst(AuthClaims.SessionId)?.Value;
        if (!await SudoRecency.IsRecentAsync(sessions, sessionId, _webAuth.SudoModeTtl).ConfigureAwait(false))
        {
            return Redirect("/account/sudo?returnUrl=/account/mfa");
        }

        await AuthFlows.PasskeyDeleteAsync(userId, id ?? string.Empty, creds, totp, users, ct).ConfigureAwait(false);
        return Redirect("/account/mfa");
    }

    public IActionResult OnGet() => Redirect("/account/mfa");
}
