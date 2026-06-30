using Freeboard.Auth;
using Freeboard.Persistence.Auth;
using Freeboard.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Freeboard.Pages;

/// <summary>
/// The sign-in page. Anonymous (not under <c>/account</c>) so it never redirect-loops. POST drives
/// the same login flow the API exposes:
/// - Full login: mint the session, set the <c>__Host-</c> session cookie, and redirect to a validated
///   local returnUrl or <c>/account</c>. A force-reset-limited session is funnelled to
///   <c>/account/complete-reset</c> to set a new password.
/// - MFA required (202): the body-only <c>mfa_token</c> is held server-side keyed by a nonce; only the
///   nonce goes into a short-lived Lax cookie, and the browser is redirected to the MFA challenge.
///   The token never reaches the client.
///
/// Errors are generic only: a wrong email, wrong password, disabled account, and a rate-limit all
/// surface the same message, so the page reveals nothing about which step failed or whether the
/// account exists.
/// </summary>
public sealed class LoginModel(
    IUserStore users,
    IPasswordCredentialStore credentials,
    IPasswordHasher hasher,
    AuthRateLimiter rateLimiter,
    SessionIssuer sessions,
    MfaChallengeService mfa,
    PendingMfaStore pendingMfa,
    IServiceProvider serviceProvider) : PageModel
{
    /// <summary>The validated returnUrl carried through GET and POST so a successful login resumes it.</summary>
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(string? email, string? password, CancellationToken ct)
    {
        var result = await AuthFlows.LoginAsync(
            email, password, HttpContext.Connection.RemoteIpAddress?.ToString(),
            users, credentials, hasher, rateLimiter, sessions, mfa, serviceProvider, ct).ConfigureAwait(false);

        switch (result)
        {
            case AuthFlows.LoginResult.Success success:
                SessionCookie.Set(
                    Response, success.Token, DateTimeOffset.UtcNow.Add(SessionLifetime()));

                // A force-reset-limited session must set a new password before anything else.
                return success.User.ForcePasswordReset
                    ? Redirect("/account/complete-reset")
                    : Redirect(Freeboard.Web.LocalRedirect.Sanitize(Url, ReturnUrl));

            case AuthFlows.LoginResult.MfaRequired mfaRequired:
                // The mfa_token stays server-side; only the nonce that points at it rides the cookie.
                // The validated local returnUrl is held alongside it so MFA completion (including the
                // magic-link email round-trip) resumes the original target, not a hardcoded default.
                var nonce = pendingMfa.Stash(
                    mfaRequired.MfaToken, Freeboard.Web.LocalRedirect.Sanitize(Url, ReturnUrl));
                SessionCookie.SetTransient(
                    Response, SessionCookie.MfaNonceName, nonce, "/", PendingMfaTtl);
                return Redirect(MfaChallengeRoute(mfaRequired.Factors));

            default:
                // RateLimited and Unauthorized both surface the same generic message (no enumeration,
                // no rate-limit oracle that distinguishes a real account).
                ModelState.AddModelError(string.Empty, GenericError);
                return Page();
        }
    }

    private static string GenericError => "Invalid email or password.";

    private static readonly TimeSpan PendingMfaTtl = TimeSpan.FromMinutes(5);

    private TimeSpan SessionLifetime()
        => serviceProvider
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<WebAuthOptions>>()
            .Value.SessionLifetime;

    /// <summary>
    /// The MFA challenge entry route. The challenge pages are added in a later group; until then this
    /// route 404s, but the pending state (nonce cookie + server-held mfa_token) is wired now.
    /// </summary>
    private static string MfaChallengeRoute(IReadOnlyList<string> factors)
    {
        var factor = factors.Count > 0 ? factors[0] : MfaFactors.Totp;
        return factor switch
        {
            MfaFactors.Passkey => "/login/mfa/passkey",
            MfaFactors.Recovery => "/login/mfa/recovery",
            MfaFactors.MagicLink => "/login/mfa/magic-link",
            _ => "/login/mfa/totp",
        };
    }
}
