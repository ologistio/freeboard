using Freeboard.Auth;
using Freeboard.Persistence.Auth;
using Freeboard.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Freeboard.Pages;

/// <summary>
/// Reset-password landing for the emailed <c>?token=</c> link. Anonymous, matching the path
/// <c>AuthEmailService</c> builds. The single-use token is kept out of the address bar and browser
/// history:
/// - <c>GET /reset-password?token=</c> is side-effect-free: it moves the token into a short-lived,
///   path-scoped, HttpOnly, Lax transient cookie and 302s to the bare path with no query string.
/// - The bare-path GET renders the new-password form from that cookie state, or a generic
///   "link expired or invalid" message when the cookie is absent.
/// - <c>POST /reset-password</c> (antiforgery-protected) consumes the token from the cookie via the
///   shared flow, then clears the cookie.
/// </summary>
public sealed class ResetPasswordModel(
    IPasswordResetStore resets,
    IPasswordCredentialStore credentials,
    IPasswordHasher hasher,
    IServiceProvider serviceProvider) : PageModel
{
    private const string CookiePath = "/reset-password";
    private static readonly TimeSpan TokenTtl = TimeSpan.FromMinutes(15);

    /// <summary>True when a scrubbed token cookie is present, so the form should render.</summary>
    public bool HasToken { get; private set; }

    /// <summary>True after a successful reset.</summary>
    public bool Reset { get; private set; }

    public IActionResult OnGet(string? token)
    {
        if (!string.IsNullOrEmpty(token))
        {
            // Scrub: stash the token in the transient cookie and 302 to the bare path so the token
            // leaves the URL before the form renders (out of history and referrers).
            SessionCookie.SetTransient(Response, SessionCookie.ResetTokenName, token, CookiePath, TokenTtl);
            return RedirectToPage();
        }

        HasToken = SessionCookie.ReadTransient(Request, SessionCookie.ResetTokenName) is not null;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? new_password, CancellationToken ct)
    {
        var token = SessionCookie.ReadTransient(Request, SessionCookie.ResetTokenName);
        if (token is null)
        {
            // The scrubbed cookie is gone (expired or never set); treat as an invalid link.
            HasToken = false;
            return Page();
        }

        var result = await AuthFlows.ResetPasswordAsync(token, new_password, resets, credentials, hasher, serviceProvider, ct)
            .ConfigureAwait(false);

        switch (result)
        {
            case AuthFlows.PasswordResult.Ok:
                SessionCookie.ClearTransient(Response, SessionCookie.ResetTokenName, CookiePath);
                Reset = true;
                return Page();
            case AuthFlows.PasswordResult.Invalid invalid when invalid.Field == "token":
                // The token was bad/expired: drop the dead cookie and show the generic link message.
                SessionCookie.ClearTransient(Response, SessionCookie.ResetTokenName, CookiePath);
                HasToken = false;
                return Page();
            case AuthFlows.PasswordResult.Invalid invalid:
                HasToken = true;
                ModelState.AddModelError(string.Empty, invalid.Message);
                return Page();
            default:
                HasToken = false;
                return Page();
        }
    }
}
