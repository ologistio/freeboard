using Freeboard.Auth;
using Freeboard.Persistence.Auth;
using Freeboard.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Freeboard.Pages.Login.Mfa;

/// <summary>
/// Shared behaviour for the login-MFA challenge factor pages (TOTP, recovery, magic-link). The login
/// step held the body-only <c>mfa_token</c> server-side keyed by a nonce; only the nonce rode the
/// cookie. Each factor page reads the nonce, looks the <c>mfa_token</c> back up here, and completes
/// the verify - the token never touches the page. On success the page issues the full session,
/// removes the nonce entry so it is single-use, clears the nonce cookie, and resumes to a validated
/// local target. When no pending challenge exists (nonce cookie absent or its server entry expired)
/// the page shows a restart message instead of attempting to verify.
/// </summary>
public abstract class MfaChallengePageModel(
    PendingMfaStore pendingMfa,
    MfaChallengeService mfa,
    IOptions<WebAuthOptions> webAuthOptions) : PageModel
{
    /// <summary>True when a pending login-MFA challenge exists, so the factor form should render.</summary>
    public bool HasPendingChallenge { get; private set; }

    /// <summary>The held <c>mfa_token</c> for the current request, or null when no pending context.</summary>
    protected string? MfaToken { get; private set; }

    /// <summary>
    /// The validated local target to resume after a successful challenge, held server-side with the
    /// mfa_token (set at login). It is never a bind property: a client-supplied returnUrl on the
    /// challenge POST must not override the one validated at login, and the magic-link email round-trip
    /// has no page query string to carry it.
    /// </summary>
    private string? ReturnUrl { get; set; }

    /// <summary>The nonce read from the cookie (used to remove the entry on completion).</summary>
    protected string? Nonce { get; private set; }

    /// <summary>The generic challenge-failed message; reveals nothing about which step failed.</summary>
    protected const string GenericError = "That code was not accepted. Try again.";

    /// <summary>
    /// Loads the pending context from the nonce cookie and confirms the challenge still resolves and
    /// offers <paramref name="factor"/>. Returns false (and leaves <see cref="HasPendingChallenge"/>
    /// false) when there is no usable pending challenge, so the caller shows the restart message.
    /// </summary>
    protected async Task<bool> LoadPendingAsync(string factor, CancellationToken ct)
    {
        Nonce = SessionCookie.ReadTransient(Request, SessionCookie.MfaNonceName);
        var pending = pendingMfa.PeekPending(Nonce);
        MfaToken = pending?.MfaToken;
        ReturnUrl = pending?.ReturnUrl;
        if (MfaToken is null)
        {
            return false;
        }

        var challenge = await mfa.ResolveAsync(MfaToken, ct).ConfigureAwait(false);
        if (challenge is null || !challenge.Factors.Split(',').Contains(factor))
        {
            return false;
        }

        HasPendingChallenge = true;
        return true;
    }

    /// <summary>
    /// Issues the full session on a successful verify, makes the nonce single-use, clears the nonce
    /// cookie, and redirects to the validated local target. The shared verify already issued the
    /// session bearer token; this only transports it as the <c>__Host-</c> session cookie.
    /// </summary>
    protected IActionResult CompleteSession(string token)
    {
        SessionCookie.Set(Response, token, DateTimeOffset.UtcNow.Add(webAuthOptions.Value.SessionLifetime));

        // Single-use: drop the server entry and the nonce cookie so the same nonce cannot re-verify.
        pendingMfa.Remove(Nonce);
        SessionCookie.ClearTransient(Response, SessionCookie.MfaNonceName, "/");

        return Redirect(Freeboard.Web.LocalRedirect.Sanitize(Url, ReturnUrl));
    }

    /// <summary>
    /// After a failed verify, decides whether the challenge was merely wrong (re-prompt) or consumed
    /// by the attempt cap (restart at <c>/login</c>). The shared flow consumes the challenge on cap
    /// exhaustion, so a now-unresolvable token means the user must start over.
    /// </summary>
    protected async Task<IActionResult> AfterFailedVerifyAsync(CancellationToken ct)
    {
        var stillPending = MfaToken is not null && await mfa.ResolveAsync(MfaToken, ct).ConfigureAwait(false) is not null;
        if (!stillPending)
        {
            // The attempt cap consumed the challenge; drop the dead nonce and restart the login.
            pendingMfa.Remove(Nonce);
            SessionCookie.ClearTransient(Response, SessionCookie.MfaNonceName, "/");
            return Redirect("/login");
        }

        HasPendingChallenge = true;
        ModelState.AddModelError(string.Empty, GenericError);
        return Page();
    }
}
