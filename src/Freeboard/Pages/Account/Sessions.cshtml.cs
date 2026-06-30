using Freeboard.Auth;
using Freeboard.Persistence.Auth;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Freeboard.Pages.Account;

/// <summary>
/// Lists the signed-in user's live sessions, backed by the same flow the API's
/// <c>users/{id}/sessions</c> exposes. The list passes the caller's own user id as the target, so a
/// user only ever sees their own sessions. Only session metadata (id, created, last-seen, expiry) is
/// rendered - never the bearer token, which is not stored in plaintext anywhere. Under <c>/account</c>,
/// so the page policy requires an authenticated session.
/// </summary>
public sealed class SessionsModel(ISessionStore sessions) : PageModel
{
    /// <summary>A session row plus whether it is the one making this request.</summary>
    public sealed record SessionView(string Id, DateTime CreatedAt, DateTime? LastSeenAt, DateTime ExpiresAt, bool IsCurrent);

    public IReadOnlyList<SessionView> Sessions { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        var userId = User.FindFirst(AuthClaims.UserId)?.Value;
        var currentId = User.FindFirst(AuthClaims.SessionId)?.Value;

        var rows = await AuthFlows.ListUserSessionsAsync(
            userId ?? string.Empty, userId, callerIsAdmin: false, sessions, ct).ConfigureAwait(false);

        Sessions = rows is null
            ? []
            : rows.Select(r => new SessionView(
                r.Id, r.CreatedAt, r.LastSeenAt, r.ExpiresAt,
                string.Equals(r.Id, currentId, StringComparison.Ordinal))).ToList();
    }
}
