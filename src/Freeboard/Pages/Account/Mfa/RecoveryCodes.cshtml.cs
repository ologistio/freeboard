using Freeboard.Web;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Freeboard.Pages.Account.Mfa;

/// <summary>
/// One-time display of freshly generated recovery codes after a first-factor enrollment (TOTP,
/// passkey) or a regenerate. The generating handler stashed the codes server-side and set only a
/// nonce cookie; this GET reads-and-clears that entry, so the codes render exactly once. A refresh
/// (or any later visit) finds nothing, because the entry and the nonce cookie are consumed here.
///
/// The codes never travel in a URL, a client-readable field, or a client-held cookie. This is the
/// single deliberate one-time display; it reuses the same server-held-nonce pattern as the login MFA
/// pending state. Under <c>/account</c>, so the page policy requires an authenticated session.
/// </summary>
public sealed class RecoveryCodesModel(RecoveryCodeDisplayStore display) : PageModel
{
    private const string CookiePath = "/account/mfa/recovery-codes";

    /// <summary>The codes to show once, or null when the nonce is absent/expired/already shown.</summary>
    public IReadOnlyList<string>? RecoveryCodes { get; private set; }

    public void OnGet()
    {
        var nonce = SessionCookie.ReadTransient(Request, SessionCookie.RecoveryDisplayName);
        RecoveryCodes = display.Take(nonce);

        // Clear the nonce cookie unconditionally so a refresh cannot re-trigger a lookup.
        SessionCookie.ClearTransient(Response, SessionCookie.RecoveryDisplayName, CookiePath);

        // The one-time codes must not be cached: the back button must not re-show them from cache.
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
    }
}
