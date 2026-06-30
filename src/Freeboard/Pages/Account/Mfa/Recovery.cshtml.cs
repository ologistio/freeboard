using Freeboard.Auth;
using Freeboard.Persistence.Auth;
using Freeboard.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Freeboard.Pages.Account.Mfa;

/// <summary>
/// Regenerates the recovery codes, invalidating the old set. POST-only, under <c>/account</c> (auth
/// required) and sudo-gated like the other MFA-management actions: it checks sudo recency with the
/// shared predicate and redirects to <c>/account/sudo</c> when the step-up is stale, before
/// regenerating. The new codes are stashed server-side and shown once on the recovery-codes display
/// page (never in the URL, a cookie, or client storage).
/// </summary>
public sealed class RecoveryModel(
    IRecoveryCodeStore recovery,
    ISessionStore sessions,
    RecoveryCodeDisplayStore recoveryDisplay,
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

        var codes = await AuthFlows.RecoveryRegenerateAsync(
            User.FindFirst(AuthClaims.UserId)?.Value, recovery, webAuthOptions, ct).ConfigureAwait(false);

        return codes is { Count: > 0 }
            ? Redirect(recoveryDisplay.StashAndRedirectTarget(Response, codes))
            : Redirect("/account/mfa");
    }

    public IActionResult OnGet() => Redirect("/account/mfa");
}
