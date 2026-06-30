using Freeboard.Api;
using Microsoft.AspNetCore.Http;

namespace Freeboard.Web;

/// <summary>
/// Bridges the session cookie to the bearer header for page routes: when a request has no
/// <c>Authorization</c> header and is not an API route, it copies the session-cookie token into
/// <c>Authorization: Bearer &lt;token&gt;</c> so the unchanged <c>BearerAuthenticationHandler</c>
/// validates it. The bridge never runs for <c>/api/v1/freeboard/*</c>, so the JSON API stays
/// bearer-header-only and carries no ambient-cookie CSRF surface; and it never overwrites a header
/// a real bearer client already sent.
///
/// Runs before <c>UseAuthentication</c>. The API-prefix test is segment-based, so a path that merely
/// shares the prefix text (for example <c>/api/v1/freeboardx</c>) is treated as a page route.
/// </summary>
public sealed class SessionCookieMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        if (!request.Headers.ContainsKey("Authorization")
            && !request.Path.StartsWithSegments(ApiRoutes.ApiRoutePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var token = SessionCookie.Read(request);
            if (token is not null)
            {
                request.Headers.Authorization = $"Bearer {token}";
            }
        }

        await next(context).ConfigureAwait(false);
    }
}
