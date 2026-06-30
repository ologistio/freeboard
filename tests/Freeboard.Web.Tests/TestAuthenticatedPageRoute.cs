using Freeboard.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Freeboard.Web.Tests;

/// <summary>
/// Maps a test-only authenticated NON-API route - <c>GET /_page-probe</c> - so the
/// cookie-to-bearer bridge can be exercised end to end before the Razor Pages host exists. The
/// route lives outside the API prefix and requires authorization, so a request authenticates only
/// if the session cookie was bridged into the bearer header. It echoes the authenticated user id.
/// Registered through the test factory as an <see cref="IStartupFilter"/> so it never ships in
/// Program.cs but still flows through the real routing/auth pipeline.
/// </summary>
internal sealed class TestAuthenticatedPageRoute : IStartupFilter
{
    public const string Path = "/_page-probe";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        next(app);
        app.UseEndpoints(endpoints =>
            endpoints.MapGet(Path, (HttpContext ctx) => ctx.User.FindFirst(AuthClaims.UserId)?.Value ?? string.Empty)
                .RequireAuthorization());
    };
}
