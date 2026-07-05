using Freeboard.Authz;
using Freeboard.Core.Authz;
using Freeboard.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Freeboard.Pages.Admin;

/// <summary>
/// One-time display of a temporary password after an admin create or reset-password. The acting
/// handler stashed the plaintext server-side and set only a nonce cookie; this GET reads-and-clears
/// that entry, so the value renders exactly once. A refresh finds nothing.
///
/// The admin-role gate runs BEFORE the take, so a non-admin holding the nonce cookie cannot consume
/// the one-time entry: the guard returns 403 before the value is claimed, leaving it for the admin's
/// own visit. The plaintext never travels in a URL, a client-readable field, or a client-held cookie.
/// </summary>
public sealed class UserCredentialModel(TempPasswordDisplayStore display, AuthzPageGuard pageGuard) : PageModel
{
    /// <summary>The temp password to show once, or null when the nonce is absent/expired/already shown.</summary>
    public string? TemporaryPassword { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await pageGuard.CheckAsync(User, AuthzActions.UserManage, AuthzResource.ForUser(null), ct) is { } denied)
        {
            return denied;
        }

        var nonce = SessionCookie.ReadTransient(Request, SessionCookie.AdminTempPasswordName);
        TemporaryPassword = display.Take(nonce);

        // Clear the nonce cookie unconditionally so a refresh cannot re-trigger a lookup.
        SessionCookie.ClearTransient(Response, SessionCookie.AdminTempPasswordName, TempPasswordDisplayStore.DisplayPath);

        // The one-time value must not be cached: the back button must not re-show it from cache.
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        return Page();
    }
}
