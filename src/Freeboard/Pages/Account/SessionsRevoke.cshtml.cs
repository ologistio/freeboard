using Freeboard.Auth;
using Freeboard.Persistence.Auth;
using Freeboard.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Freeboard.Pages.Account;

/// <summary>
/// Revokes one session by id, or all the user's sessions. POST-only, under <c>/account</c> (auth
/// required). Backed by the same IDOR-safe flows the API exposes: revoking one passes the caller's own
/// user id, so a user can only revoke their own session (a foreign or unknown id is a no-op and lands
/// back on the list). Revoking the CURRENT session (or all sessions) clears the <c>__Host-</c> session
/// cookie and redirects to <c>/login</c>, because the bearer this browser holds is now dead.
/// </summary>
public sealed class SessionsRevokeModel(ISessionStore sessions) : PageModel
{
    public async Task<IActionResult> OnPostAsync(string? sessionId, bool all, CancellationToken ct)
    {
        var userId = User.FindFirst(AuthClaims.UserId)?.Value ?? string.Empty;
        var currentId = User.FindFirst(AuthClaims.SessionId)?.Value;

        if (all)
        {
            await AuthFlows.DeleteUserSessionsAsync(userId, userId, callerIsAdmin: false, sessions, ct)
                .ConfigureAwait(false);
            SessionCookie.Clear(Response);
            return Redirect("/login");
        }

        // DeleteSessionAsync only deletes a session the caller owns; a foreign or unknown id is a no-op.
        await AuthFlows.DeleteSessionAsync(sessionId ?? string.Empty, userId, callerIsAdmin: false, sessions, ct)
            .ConfigureAwait(false);

        // Revoking this browser's own session kills the bearer it holds, so clear the cookie and sign out.
        if (string.Equals(sessionId, currentId, StringComparison.Ordinal))
        {
            SessionCookie.Clear(Response);
            return Redirect("/login");
        }

        return Redirect("/account/sessions");
    }

    public IActionResult OnGet() => Redirect("/account/sessions");
}
