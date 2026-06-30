using Freeboard.Auth;
using Freeboard.Persistence.Auth;
using Freeboard.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Freeboard.Pages.Auth;

/// <summary>
/// Login-MFA magic-link landing for the emailed <c>?token=</c> link. Anonymous, matching the path
/// <c>AuthEmailService</c> builds. Side-effect-free GET: it scrubs the link token out of the URL into
/// a short-lived Lax transient cookie and 302s to the bare path, so the single-use token never sits
/// in browser history or referrers. The bare-path GET renders a continue form from the pending state.
///
/// The antiforgery-protected POST completes the login-MFA verify using the held login <c>mfa_token</c>
/// (looked up via the nonce cookie set at login) plus the scrubbed link token. There is no
/// login-vs-sudo discriminator: sudo magic-link is out of web scope, so this landing always completes
/// the login-MFA verify. On success it issues the full session, makes the nonce single-use, clears
/// both transient cookies, and resumes the local target captured at login. When the attempt cap
/// consumes the challenge it restarts at <c>/login</c> (dropping the dead nonce and both transient
/// cookies), mirroring the other MFA factors. When no pending login-MFA context exists - the link was
/// opened in a different browser (no nonce cookie) or the server entry expired - it shows a restart
/// message.
/// </summary>
public sealed class MagicLinkModel(
    PendingMfaStore pendingMfa,
    MfaChallengeService mfa,
    IMfaChallengeStore challenges,
    IUserStore users,
    AuthRateLimiter rateLimiter,
    IOptions<WebAuthOptions> webAuthOptions) : PageModel
{
    private const string CookiePath = "/auth/magic-link";
    private static readonly TimeSpan TokenTtl = TimeSpan.FromMinutes(15);

    /// <summary>True when a pending login-MFA context and a scrubbed link token are both present.</summary>
    public bool CanComplete { get; private set; }

    public IActionResult OnGet(string? token)
    {
        if (!string.IsNullOrEmpty(token))
        {
            // Scrub: stash the link token in the transient cookie and 302 to the bare path so the
            // token leaves the URL before anything renders (out of history and referrers).
            SessionCookie.SetTransient(Response, SessionCookie.MagicLinkTokenName, token, CookiePath, TokenTtl);
            return RedirectToPage();
        }

        CanComplete = PendingContextPresent();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var nonce = SessionCookie.ReadTransient(Request, SessionCookie.MfaNonceName);
        var pending = pendingMfa.PeekPending(nonce);
        var linkToken = SessionCookie.ReadTransient(Request, SessionCookie.MagicLinkTokenName);
        if (pending is null || linkToken is null)
        {
            // No pending login-MFA context (different browser or expired entry): show the restart message.
            CanComplete = false;
            return Page();
        }

        var result = await AuthFlows.MfaVerifyAsync(
            pending.MfaToken, MfaFactors.MagicLink, HttpContext.Connection.RemoteIpAddress?.ToString(),
            challenge => challenges.VerifyMagicLinkAsync(challenge.Id, linkToken, DateTime.UtcNow, ct),
            mfa, users, rateLimiter, ct).ConfigureAwait(false);

        if (result is AuthFlows.MfaVerifyResult.Success success)
        {
            SessionCookie.Set(
                Response, success.Token, DateTimeOffset.UtcNow.Add(webAuthOptions.Value.SessionLifetime));

            // Single-use: drop the server entry and clear both transient cookies.
            pendingMfa.Remove(nonce);
            SessionCookie.ClearTransient(Response, SessionCookie.MfaNonceName, "/");
            SessionCookie.ClearTransient(Response, SessionCookie.MagicLinkTokenName, CookiePath);
            return Redirect(Freeboard.Web.LocalRedirect.Sanitize(Url, pending.ReturnUrl));
        }

        // A failed verify is either a wrong/expired link (re-prompt, keep the nonce so a fresh link can
        // be sent) or the attempt cap consuming the challenge (mirror the other factors: restart at
        // /login, dropping the dead nonce and both transient cookies).
        SessionCookie.ClearTransient(Response, SessionCookie.MagicLinkTokenName, CookiePath);
        if (await mfa.ResolveAsync(pending.MfaToken, ct).ConfigureAwait(false) is null)
        {
            pendingMfa.Remove(nonce);
            SessionCookie.ClearTransient(Response, SessionCookie.MfaNonceName, "/");
            return Redirect("/login");
        }

        CanComplete = false;
        return Page();
    }

    private bool PendingContextPresent()
    {
        var nonce = SessionCookie.ReadTransient(Request, SessionCookie.MfaNonceName);
        return pendingMfa.Peek(nonce) is not null
            && SessionCookie.ReadTransient(Request, SessionCookie.MagicLinkTokenName) is not null;
    }
}
