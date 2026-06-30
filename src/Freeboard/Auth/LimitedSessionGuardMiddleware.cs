using System.Globalization;
using System.Text.Json;
using Freeboard.Api;
using Freeboard.Persistence.Auth;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Freeboard.Auth;

/// <summary>
/// Enforces the force-reset (limited) session allowlist. When the authenticated
/// session is <see cref="SessionAuthState.ForceResetLimited"/>, only
/// <c>GET {prefix}/auth/me</c>, <c>POST {prefix}/auth/logout</c>, and
/// <c>POST {prefix}/account/password</c> are permitted, plus any page route carrying the
/// <see cref="LimitedSessionAllowed"/> marker (the forced-reset completion page, logout, and the
/// account landing) so a limited browser session can complete the reset funnel.
///
/// Any other request from a limited session is blocked. Only a matched Razor Page route is
/// redirected to <c>/account/complete-reset</c> so the browser is funnelled to set a new password;
/// every other request - the API prefix, the <c>/</c> minimal endpoint, any other non-page endpoint,
/// and unmatched routes - gets the JSON 403 problem+details. The redirect lives here because this
/// middleware terminates the request before the page authentication scheme could turn a 403 into a
/// redirect. Runs after authentication and before endpoint execution.
/// </summary>
public sealed class LimitedSessionGuardMiddleware(RequestDelegate next)
{
    private static readonly (string Method, string Path)[] Allowed =
    [
        (HttpMethods.Get, ApiRoutes.ApiRoutePrefix + "/auth/me"),
        (HttpMethods.Post, ApiRoutes.ApiRoutePrefix + "/auth/logout"),
        (HttpMethods.Post, ApiRoutes.ApiRoutePrefix + "/account/password"),
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated != true || !IsLimited(user))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        // A page route the limited session needs (complete-reset, logout, account landing) carries
        // the marker; permit it in addition to the exact-path API allowlist, which is unchanged.
        if (context.GetEndpoint()?.Metadata.GetMetadata<LimitedSessionAllowed>() is not null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var method = context.Request.Method;
        var path = context.Request.Path.Value ?? string.Empty;
        foreach (var (allowedMethod, allowedPath) in Allowed)
        {
            if (string.Equals(method, allowedMethod, StringComparison.OrdinalIgnoreCase)
                && string.Equals(path, allowedPath, StringComparison.Ordinal))
            {
                await next(context).ConfigureAwait(false);
                return;
            }
        }

        // Only a Razor Page route is funnelled to the reset page with a 302 (a browser needs the
        // redirect). Everything else - the API prefix, the `/` minimal endpoint, any other non-page
        // endpoint, and unmatched routes - keeps the byte-identical JSON 403, exactly as before.
        if (context.GetEndpoint()?.Metadata.GetMetadata<PageActionDescriptor>() is not null)
        {
            context.Response.Redirect("/account/complete-reset");
            return;
        }

        await WriteForbiddenAsync(context).ConfigureAwait(false);
    }

    private static bool IsLimited(System.Security.Claims.ClaimsPrincipal user)
    {
        var raw = user.FindFirst(AuthClaims.AuthState)?.Value;
        return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var state)
            && state == (int)SessionAuthState.ForceResetLimited;
    }

    private static async Task WriteForbiddenAsync(HttpContext context)
    {
        var body = new Dictionary<string, object>
        {
            ["type"] = "https://freeboard.io/problems/password-reset-required",
            ["title"] = "Password reset required",
            ["status"] = StatusCodes.Status403Forbidden,
            ["detail"] = "This session must set a new password before using other endpoints.",
        };
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(body)).ConfigureAwait(false);
    }
}
