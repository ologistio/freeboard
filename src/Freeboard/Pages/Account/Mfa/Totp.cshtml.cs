using Freeboard.Auth;
using Freeboard.Persistence.Auth;
using Freeboard.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Freeboard.Pages.Account.Mfa;

/// <summary>
/// TOTP (authenticator app) enroll and activate. Under <c>/account</c> (auth required) and sudo-gated:
/// pipeline sudo policies do not run for in-process page handlers, so this page checks sudo recency
/// itself with the shared predicate and redirects to <c>/account/sudo</c> on a stale step-up, without
/// performing the action.
///
/// The secret is staged exactly once per enrollment attempt: the GET has no side effect, and an
/// explicit "set up" POST begins enrollment (so a page reload cannot re-stage and rotate the secret).
/// The staged provisioning URI is held server-side keyed by a nonce cookie; an activation retry reads
/// the SAME URI back, so a wrong confirming code never rotates the secret and invalidates the QR the
/// user already scanned. The activation code is verified against the already-staged secret. On a
/// first-factor activation the backend returns one-time recovery codes, stashed server-side and shown
/// once on the recovery-codes display page (never in the URL, a cookie, or client storage).
/// </summary>
public sealed class TotpModel(
    ITotpStore totp,
    IWebAuthnCredentialStore webAuthn,
    IRecoveryCodeStore recovery,
    IUserStore users,
    ISessionStore sessions,
    RecoveryCodeDisplayStore recoveryDisplay,
    TotpEnrollmentDisplayStore enrollDisplay,
    IOptions<WebAuthOptions> webAuthOptions) : PageModel
{
    private readonly WebAuthOptions _webAuth = webAuthOptions.Value;

    /// <summary>The otpauth:// provisioning URI to add to an authenticator app, when enrollment is in progress.</summary>
    public string? ProvisioningUri { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await RequireSudoAsync().ConfigureAwait(false) is { } redirect)
        {
            return redirect;
        }

        // Side-effect-free: read back the staged URI if an enrollment is already in progress. A bare
        // GET (no staged secret) shows the "set up" button instead, so a reload cannot rotate the secret.
        ProvisioningUri = enrollDisplay.Peek(SessionCookie.ReadTransient(Request, SessionCookie.TotpEnrollName));
        NoStore();
        return Page();
    }

    /// <summary>Begins enrollment: generates the secret once, stages it, and redirects back to the form.</summary>
    public async Task<IActionResult> OnPostBeginAsync(CancellationToken ct)
    {
        if (await RequireSudoAsync().ConfigureAwait(false) is { } redirect)
        {
            return redirect;
        }

        var uri = await AuthFlows.TotpEnrollAsync(UserId, totp, users, ct).ConfigureAwait(false);
        if (uri is not null)
        {
            enrollDisplay.Begin(Response, uri);
        }

        return Redirect("/account/mfa/totp");
    }

    public async Task<IActionResult> OnPostAsync(string? code, CancellationToken ct)
    {
        if (await RequireSudoAsync().ConfigureAwait(false) is { } redirect)
        {
            return redirect;
        }

        // The code is verified against the already-staged secret; do NOT re-enroll here.
        var result = await AuthFlows.TotpActivateAsync(
            UserId, code, totp, webAuthn, recovery, users, webAuthOptions, ct).ConfigureAwait(false);

        switch (result)
        {
            case AuthFlows.FactorActivationResult.Activated activated:
                enrollDisplay.End(Request, Response);
                return activated.RecoveryCodes is { Count: > 0 } codes
                    ? Redirect(recoveryDisplay.StashAndRedirectTarget(Response, codes))
                    : Redirect("/account/mfa");
            case AuthFlows.FactorActivationResult.Invalid invalid:
                ModelState.AddModelError(string.Empty, invalid.Message);
                // Re-show the SAME staged provisioning URI so the user can retry without rotating the secret.
                ProvisioningUri = enrollDisplay.Peek(SessionCookie.ReadTransient(Request, SessionCookie.TotpEnrollName));
                NoStore();
                return Page();
            default:
                return Redirect("/account/mfa");
        }
    }

    private string? UserId => User.FindFirst(AuthClaims.UserId)?.Value;

    /// <summary>Stops a browser or proxy caching the page that renders the provisioning secret.</summary>
    private void NoStore()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
    }

    private async Task<IActionResult?> RequireSudoAsync()
    {
        var sessionId = User.FindFirst(AuthClaims.SessionId)?.Value;
        return await SudoRecency.IsRecentAsync(sessions, sessionId, _webAuth.SudoModeTtl).ConfigureAwait(false)
            ? null
            : Redirect("/account/sudo?returnUrl=/account/mfa/totp");
    }
}
