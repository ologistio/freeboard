using System.Security.Claims;
using System.Text.Json.Serialization;
using Freeboard.Api;
using Freeboard.Persistence.Auth;

namespace Freeboard.Auth;

/// <summary>
/// The Freeboard-native auth and session HTTP endpoints, all under
/// <see cref="ApiRoutes.ApiRoutePrefix"/> and all tagged with the
/// <see cref="AuthEndpoint"/> marker so the GitOps read-only exemption applies. Endpoints use
/// the persistence stores via DI and the web plumbing (bearer handler, rate limiter, session
/// issuer, response helpers).
/// </summary>
public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var auth = app.MapGroup(ApiRoutes.ApiRoutePrefix);

        auth.MapPost("/auth/login", LoginAsync).MarkAuthEndpoint();
        auth.MapGet("/auth/me", Me).RequireAuthorization().MarkAuthEndpoint();
        auth.MapPost("/auth/logout", LogoutAsync).RequireAuthorization().MarkAuthEndpoint();
        auth.MapPost("/auth/password/change", ChangePasswordAsync).RequireAuthorization().MarkAuthEndpoint();
        auth.MapPost("/auth/password/forgot", ForgotPasswordAsync).MarkAuthEndpoint();
        auth.MapPost("/auth/password/reset", ResetPasswordAsync).MarkAuthEndpoint();
        auth.MapPost("/account/password", AccountPasswordAsync).RequireAuthorization().MarkAuthEndpoint();

        auth.MapGet("/auth/sessions/{id}", GetSessionAsync).RequireAuthorization().MarkAuthEndpoint();
        auth.MapDelete("/auth/sessions/{id}", DeleteSessionAsync).RequireAuthorization().MarkAuthEndpoint();
        auth.MapGet("/users/{id}/sessions", ListUserSessionsAsync).RequireAuthorization().MarkAuthEndpoint();
        auth.MapDelete("/users/{id}/sessions", DeleteUserSessionsAsync).RequireAuthorization().MarkAuthEndpoint();

        // First-admin bootstrap lives at /setup, not under /auth.
        auth.MapPost("/setup", BootstrapAsync).MarkAuthEndpoint();
    }

    public sealed record LoginRequest(string? Email, string? Password);

    private static async Task<IResult> LoginAsync(
        LoginRequest body,
        HttpContext ctx,
        IUserStore users,
        IPasswordCredentialStore credentials,
        IPasswordHasher hasher,
        AuthRateLimiter rateLimiter,
        SessionIssuer sessions,
        MfaChallengeService mfa,
        IServiceProvider sp,
        CancellationToken ct)
    {
        var result = await AuthFlows.LoginAsync(
            body?.Email, body?.Password, ctx.Connection.RemoteIpAddress?.ToString(),
            users, credentials, hasher, rateLimiter, sessions, mfa, sp, ct).ConfigureAwait(false);
        return result switch
        {
            AuthFlows.LoginResult.RateLimited r => AuthRateLimiter.Throttled(r.Outcome),
            AuthFlows.LoginResult.Unauthorized => GenericUnauthorized(),
            AuthFlows.LoginResult.MfaRequired m => Results.Json(
                new { mfa_required = true, mfa_token = m.MfaToken, factors = m.Factors },
                statusCode: StatusCodes.Status202Accepted),
            AuthFlows.LoginResult.Success s => Results.Ok(new { user = ApiResponses.UserObject(s.User), token = s.Token }),
            _ => GenericUnauthorized(),
        };
    }

    private static async Task<IResult> Me(HttpContext ctx, IUserStore users, CancellationToken ct)
    {
        var user = await AuthFlows.MeAsync(
            ctx.User.FindFirst(AuthClaims.UserId)?.Value, users, ct).ConfigureAwait(false);
        return user is null ? GenericUnauthorized() : Results.Ok(ApiResponses.UserObject(user));
    }

    private static async Task<IResult> LogoutAsync(HttpContext ctx, ISessionStore sessions, CancellationToken ct)
    {
        await AuthFlows.LogoutAsync(ctx.User.FindFirst(AuthClaims.SessionId)?.Value, sessions, ct).ConfigureAwait(false);
        return Results.Ok(new { logged_out = true });
    }

    public sealed record ChangePasswordRequest(
        [property: JsonPropertyName("old_password")] string? OldPassword,
        [property: JsonPropertyName("new_password")] string? NewPassword);

    private static async Task<IResult> ChangePasswordAsync(
        ChangePasswordRequest body,
        HttpContext ctx,
        IPasswordCredentialStore credentials,
        IPasswordHasher hasher,
        IServiceProvider sp,
        CancellationToken ct)
    {
        var result = await AuthFlows.ChangePasswordAsync(
            ctx.User.FindFirst(AuthClaims.UserId)?.Value,
            ctx.User.FindFirst(AuthClaims.SessionId)?.Value,
            body?.OldPassword, body?.NewPassword, credentials, hasher, sp, ct).ConfigureAwait(false);
        return result switch
        {
            AuthFlows.PasswordResult.Unauthorized => GenericUnauthorized(),
            AuthFlows.PasswordResult.Invalid v => ApiResponses.ValidationProblem(v.Field, v.Message),
            AuthFlows.PasswordResult.Ok => Results.Ok(new { password_changed = true }),
            _ => GenericUnauthorized(),
        };
    }

    public sealed record ForgotPasswordRequest(string? Email);

    private static async Task<IResult> ForgotPasswordAsync(
        ForgotPasswordRequest body,
        IUserStore users,
        IPasswordResetStore resets,
        Microsoft.Extensions.Options.IOptions<WebAuthOptions> options,
        ILoggerFactory loggerFactory,
        IServiceProvider sp,
        CancellationToken ct)
    {
        await AuthFlows.ForgotPasswordAsync(body?.Email, users, resets, options, loggerFactory, sp, ct)
            .ConfigureAwait(false);
        return Results.Ok(new { ok = true });
    }

    public sealed record ResetPasswordRequest(
        string? Token,
        [property: JsonPropertyName("new_password")] string? NewPassword);

    private static async Task<IResult> ResetPasswordAsync(
        ResetPasswordRequest body,
        IPasswordResetStore resets,
        IPasswordCredentialStore credentials,
        IPasswordHasher hasher,
        IServiceProvider sp,
        CancellationToken ct)
    {
        var result = await AuthFlows.ResetPasswordAsync(
            body?.Token, body?.NewPassword, resets, credentials, hasher, sp, ct).ConfigureAwait(false);
        return result switch
        {
            AuthFlows.PasswordResult.Invalid v => ApiResponses.ValidationProblem(v.Field, v.Message),
            AuthFlows.PasswordResult.Ok => Results.Ok(new { password_reset = true }),
            _ => GenericUnauthorized(),
        };
    }

    public sealed record AccountPasswordRequest(
        [property: JsonPropertyName("new_password")] string? NewPassword);

    private static async Task<IResult> AccountPasswordAsync(
        AccountPasswordRequest body,
        HttpContext ctx,
        IUserStore users,
        IPasswordCredentialStore credentials,
        IPasswordHasher hasher,
        IServiceProvider sp,
        CancellationToken ct)
    {
        var result = await AuthFlows.AccountPasswordAsync(
            ctx.User.FindFirst(AuthClaims.UserId)?.Value,
            ctx.User.FindFirst(AuthClaims.SessionId)?.Value,
            IsForceResetLimited(ctx.User),
            body?.NewPassword, users, credentials, hasher, sp, ct).ConfigureAwait(false);
        return result switch
        {
            AuthFlows.PasswordResult.Unauthorized => GenericUnauthorized(),
            AuthFlows.PasswordResult.Forbidden f => Forbidden(f.Detail),
            AuthFlows.PasswordResult.Invalid v => ApiResponses.ValidationProblem(v.Field, v.Message),
            AuthFlows.PasswordResult.Ok => Results.Ok(new { password_set = true }),
            _ => GenericUnauthorized(),
        };
    }

    private static async Task<IResult> GetSessionAsync(
        string id, HttpContext ctx, ISessionStore sessions, CancellationToken ct)
    {
        var session = await AuthFlows.GetSessionAsync(id, CallerUserId(ctx), IsAdmin(ctx.User), sessions, ct)
            .ConfigureAwait(false);
        return session is null ? Results.NotFound() : Results.Ok(SessionObject(session));
    }

    private static async Task<IResult> DeleteSessionAsync(
        string id, HttpContext ctx, ISessionStore sessions, CancellationToken ct)
    {
        var deleted = await AuthFlows.DeleteSessionAsync(id, CallerUserId(ctx), IsAdmin(ctx.User), sessions, ct)
            .ConfigureAwait(false);
        return deleted ? Results.Ok(new { deleted = true }) : Results.NotFound();
    }

    private static async Task<IResult> ListUserSessionsAsync(
        string id, HttpContext ctx, ISessionStore sessions, CancellationToken ct)
    {
        var rows = await AuthFlows.ListUserSessionsAsync(id, CallerUserId(ctx), IsAdmin(ctx.User), sessions, ct)
            .ConfigureAwait(false);
        return rows is null ? Results.NotFound() : Results.Ok(rows.Select(SessionObject));
    }

    private static async Task<IResult> DeleteUserSessionsAsync(
        string id, HttpContext ctx, ISessionStore sessions, CancellationToken ct)
    {
        var removed = await AuthFlows.DeleteUserSessionsAsync(id, CallerUserId(ctx), IsAdmin(ctx.User), sessions, ct)
            .ConfigureAwait(false);
        return removed is null ? Results.NotFound() : Results.Ok(new { deleted = removed.Value });
    }

    public sealed record BootstrapRequest(
        string? Email,
        string? Name,
        string? Password,
        [property: JsonPropertyName("bootstrap_secret")] string? BootstrapSecret);

    private static async Task<IResult> BootstrapAsync(
        BootstrapRequest body,
        HttpContext ctx,
        IUserStore users,
        IPasswordHasher hasher,
        AuthRateLimiter rateLimiter,
        SessionIssuer sessions,
        Microsoft.Extensions.Options.IOptions<WebAuthOptions> options,
        IServiceProvider sp,
        CancellationToken ct)
    {
        // The presented secret comes from the body or the bootstrap header; the rest of the gate
        // (constant-time compare, rate limit, bootstrap) lives in the shared flow.
        var presented = body?.BootstrapSecret ?? ctx.Request.Headers["X-Freeboard-Bootstrap-Secret"].ToString();
        var result = await AuthFlows.BootstrapAsync(
            body?.Email, body?.Name, body?.Password, presented,
            ctx.Connection.RemoteIpAddress?.ToString(),
            users, hasher, rateLimiter, sessions, options, sp, ct).ConfigureAwait(false);
        return result switch
        {
            AuthFlows.BootstrapResult.Unauthorized => GenericUnauthorized(),
            AuthFlows.BootstrapResult.RateLimited r => AuthRateLimiter.Throttled(r.Outcome),
            AuthFlows.BootstrapResult.Invalid v => ApiResponses.ValidationProblem(v.Errors),
            AuthFlows.BootstrapResult.AlreadyInitialized => Results.Conflict(new { error = "already_initialized" }),
            AuthFlows.BootstrapResult.Created c => Results.Json(
                new { user = ApiResponses.UserObject(c.User), token = c.Token },
                statusCode: StatusCodes.Status201Created),
            _ => GenericUnauthorized(),
        };
    }

    // ---- helpers ----

    private static IResult GenericUnauthorized() => Results.Json(
        new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);

    private static IResult Forbidden(string detail) => Results.Json(
        new { error = "forbidden", detail }, statusCode: StatusCodes.Status403Forbidden);

    /// <summary>True when the session's auth-state claim is force-reset-limited.</summary>
    private static bool IsForceResetLimited(ClaimsPrincipal user)
    {
        var raw = user.FindFirst(AuthClaims.AuthState)?.Value;
        return int.TryParse(raw, System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture, out var state)
            && state == (int)SessionAuthState.ForceResetLimited;
    }

    private static bool IsAdmin(ClaimsPrincipal user)
        => string.Equals(user.FindFirst(AuthClaims.Role)?.Value, GlobalRoles.Admin, StringComparison.Ordinal);

    private static string? CallerUserId(HttpContext ctx) => ctx.User.FindFirst(AuthClaims.UserId)?.Value;

    private static object SessionObject(SessionRow s) => new
    {
        id = s.Id,
        user_id = s.UserId,
        auth_state = (int)s.AuthState,
        created_at = s.CreatedAt,
        expires_at = s.ExpiresAt,
        last_seen_at = s.LastSeenAt,
        sudo_at = s.SudoAt,
    };
}
