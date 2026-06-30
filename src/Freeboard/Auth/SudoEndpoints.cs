using Freeboard.Api;
using Freeboard.Persistence.Auth;
using Microsoft.Extensions.Options;

namespace Freeboard.Auth;

/// <summary>
/// Sudo-mode step-up. <c>POST /auth/sudo</c> re-confirms ANY of the caller's
/// currently-usable factors - the SAME set the login MFA challenge accepts (passkey, TOTP, recovery,
/// or magic-link fallback) - or a password re-confirm for a non-MFA user, and on success stamps
/// <c>sessions.sudo_at = now</c>. It is rate-limited. A magic-link-only user can step up via
/// magic-link (the send/verify sub-endpoints) to then enroll a strong factor. The
/// <see cref="RequireSudoModeRequirement"/> policy reads the resulting sudo_at.
/// </summary>
public static class SudoEndpoints
{
    public static void MapSudoEndpoints(this WebApplication app)
    {
        var g = app.MapGroup(ApiRoutes.ApiRoutePrefix);

        g.MapPost("/auth/sudo", SudoAsync).RequireAuthorization().MarkAuthEndpoint();
        g.MapPost("/auth/sudo/passkey/options", SudoPasskeyOptionsAsync).RequireAuthorization().MarkAuthEndpoint();
        g.MapPost("/auth/sudo/magic-link/send", SudoMagicLinkSendAsync).RequireAuthorization().MarkAuthEndpoint();
    }

    private static async Task<IResult> SudoAsync(
        HttpContext ctx,
        IUserStore users,
        IPasswordCredentialStore credentials,
        IPasswordHasher hasher,
        ITotpStore totp,
        IRecoveryCodeStore recovery,
        WebAuthnCeremony webAuthn,
        WebAuthnEnrollmentStore enrollment,
        IMfaChallengeStore challenges,
        ISessionStore sessions,
        AuthRateLimiter rateLimiter,
        CancellationToken ct)
    {
        // Reject an unauthenticated or stale-user request with the uniform 401 before doing any work.
        var userId = ctx.User.FindFirst(AuthClaims.UserId)?.Value;
        var sessionId = ctx.User.FindFirst(AuthClaims.SessionId)?.Value;
        if (userId is null || sessionId is null || await users.GetByIdAsync(userId, ct).ConfigureAwait(false) is null)
        {
            return Unauthorized();
        }

        // Read the raw body once so the opaque passkey assertion can be passed through verbatim.
        using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;

        var result = await AuthFlows.SudoAsync(
            userId,
            sessionId,
            ClientIp(ctx),
            Prop(root, "factor"),
            Prop(root, "password"),
            Prop(root, "code"),
            Prop(root, "recovery_code"),
            Prop(root, "correlation"),
            Payload(root, "assertion"),
            Prop(root, "challenge_id"),
            Prop(root, "link_token"),
            users, credentials, hasher, totp, recovery, webAuthn, enrollment, challenges, sessions, rateLimiter, ct)
            .ConfigureAwait(false);
        return result switch
        {
            AuthFlows.SudoResult.RateLimited r => AuthRateLimiter.Throttled(r.Outcome),
            AuthFlows.SudoResult.Ok => Results.Ok(new { sudo = true }),
            _ => Unauthorized(),
        };
    }

    /// <summary>Returns assertion options + a correlation token for a passkey sudo step.</summary>
    private static async Task<IResult> SudoPasskeyOptionsAsync(
        HttpContext ctx, WebAuthnCeremony webAuthn, WebAuthnEnrollmentStore store, CancellationToken ct)
    {
        var result = await AuthFlows.SudoPasskeyOptionsAsync(
            ctx.User.FindFirst(AuthClaims.UserId)?.Value, webAuthn, store, ct).ConfigureAwait(false);
        return result switch
        {
            AuthFlows.SudoPasskeyOptionsResult.Unauthorized => Unauthorized(),
            AuthFlows.SudoPasskeyOptionsResult.WebAuthnUnconfigured => Results.Json(
                new { error = "webauthn_unconfigured" }, statusCode: StatusCodes.Status503ServiceUnavailable),
            AuthFlows.SudoPasskeyOptionsResult.Ok ok => Results.Content(
                $"{{\"correlation\":\"{ok.Correlation}\",\"options\":{ok.OptionsJson}}}", "application/json"),
            _ => Unauthorized(),
        };
    }

    /// <summary>
    /// Mints (or REUSES) a sudo magic-link challenge for the user and emails a prefixless token.
    /// Magic-link is only allowed when it is one of the user's currently-available factors
    /// (mfa_enabled + no passkey + no TOTP + sender) - a user with a passkey/TOTP must use that factor.
    /// The find-or-create + send-count increment is one atomic store call, so concurrent first
    /// sends cannot create multiple active challenges and bypass MagicLinkMaxSends.
    /// </summary>
    private static async Task<IResult> SudoMagicLinkSendAsync(
        HttpContext ctx, IUserStore users, IMfaChallengeStore challenges, IPasswordCredentialStore credentials,
        MfaFactorService mfaFactors, ITokenHasher tokenHasher, AuthRateLimiter rateLimiter,
        IOptions<WebAuthOptions> options, IServiceProvider sp, CancellationToken ct)
    {
        var result = await AuthFlows.SudoMagicLinkSendAsync(
            ctx.User.FindFirst(AuthClaims.UserId)?.Value, ClientIp(ctx),
            users, challenges, credentials, mfaFactors, tokenHasher, rateLimiter, options, sp, ct)
            .ConfigureAwait(false);
        return result switch
        {
            AuthFlows.SudoMagicLinkSendResult.Unauthorized => Unauthorized(),
            AuthFlows.SudoMagicLinkSendResult.Unavailable => Results.Json(
                new { error = "magic_link_unavailable" }, statusCode: StatusCodes.Status400BadRequest),
            AuthFlows.SudoMagicLinkSendResult.RateLimited r => AuthRateLimiter.Throttled(r.Outcome),
            AuthFlows.SudoMagicLinkSendResult.SendCapReached => Results.Json(
                new { error = "send_cap_reached" }, statusCode: StatusCodes.Status429TooManyRequests),
            // The caller submits { factor: magic_link, challenge_id, link_token } to /auth/sudo.
            AuthFlows.SudoMagicLinkSendResult.Sent s => Results.Ok(new { challenge_id = s.ChallengeId }),
            _ => Unauthorized(),
        };
    }

    private static string? Prop(System.Text.Json.JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;

    private static string? Payload(System.Text.Json.JsonElement root, string name)
        => root.TryGetProperty(name, out var v) ? v.GetRawText() : null;

    private static string? ClientIp(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString();

    private static IResult Unauthorized() => Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);
}
