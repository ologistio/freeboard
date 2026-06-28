using System.Globalization;
using System.Text.Json;
using Freeboard.Api;
using Freeboard.Persistence.Auth;

namespace Freeboard.Auth;

/// <summary>
/// Enforces the force-reset (limited) session allowlist. When the authenticated
/// session is <see cref="SessionAuthState.ForceResetLimited"/>, only
/// <c>GET {prefix}/auth/me</c>, <c>POST {prefix}/auth/logout</c>, and
/// <c>POST {prefix}/account/password</c> are permitted; any other bearer-protected request
/// returns 403 so a forced-reset user cannot use the rest of the API until they set a new
/// password. Runs after authentication and before endpoint execution.
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
