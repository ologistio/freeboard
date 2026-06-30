using Freeboard.Auth;
using Freeboard.Persistence.Auth;
using Freeboard.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Freeboard.Pages;

/// <summary>
/// Signs the browser out. Anonymous so a partially authenticated or limited session can still reach
/// it. POST revokes the server session and clears the <c>__Host-</c> session cookie
/// unconditionally - even if the server-side delete is a no-op (already revoked, or no session
/// claim) - so the browser never keeps a cookie that points at a dead session.
/// </summary>
public sealed class LogoutModel(ISessionStore sessions) : PageModel
{
    public IActionResult OnGet() => Redirect("/login");

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        await AuthFlows.LogoutAsync(User.FindFirst(AuthClaims.SessionId)?.Value, sessions, ct).ConfigureAwait(false);
        SessionCookie.Clear(Response);
        return Page();
    }
}
