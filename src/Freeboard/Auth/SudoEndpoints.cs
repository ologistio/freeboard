using System.Text.Json.Serialization;
using Freeboard.Api;
using Freeboard.Persistence.Auth;
using Microsoft.Extensions.DependencyInjection;
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

    public sealed record SudoRequest(
        string? Factor,
        string? Password,
        string? Code,
        [property: JsonPropertyName("recovery_code")] string? RecoveryCode,
        string? Correlation,
        object? Assertion,
        [property: JsonPropertyName("link_token")] string? LinkToken);

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
        // userId binds the magic-link verify/consume to the CURRENT bearer user.
        var userId = ctx.User.FindFirst(AuthClaims.UserId)?.Value;
        var sessionId = ctx.User.FindFirst(AuthClaims.SessionId)?.Value;
        if (userId is null || sessionId is null)
        {
            return Unauthorized();
        }

        var user = await users.GetByIdAsync(userId, ct).ConfigureAwait(false);
        if (user is null)
        {
            return Unauthorized();
        }

        // Read the raw body once so the opaque passkey assertion can be passed through verbatim.
        using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;
        var factor = Prop(root, "factor");

        var limited = await rateLimiter.CheckAsync(user.EmailNormalized, ClientIp(ctx), ct).ConfigureAwait(false);
        if (limited.Limited)
        {
            return AuthRateLimiter.Throttled(limited);
        }

        var ok = factor switch
        {
            // Non-MFA users (or an explicit password step) re-confirm the password.
            "password" or null when !user.MfaEnabled => await VerifyPasswordAsync(credentials, hasher, userId, Prop(root, "password"), ct).ConfigureAwait(false),
            MfaFactors.Totp => await totp.VerifyAsync(userId, Prop(root, "code") ?? string.Empty, ct).ConfigureAwait(false),
            MfaFactors.Recovery => await recovery.ConsumeAsync(userId, Prop(root, "recovery_code") ?? string.Empty, ct).ConfigureAwait(false),
            MfaFactors.Passkey => await VerifyPasskeyAsync(webAuthn, enrollment, userId, Prop(root, "correlation"), Payload(root, "assertion"), ct).ConfigureAwait(false),
            // Atomic single-use consume, bound to the current bearer user.
            MfaFactors.MagicLink => await challenges.VerifyAndConsumeMagicLinkAsync(
                Prop(root, "challenge_id") ?? string.Empty, userId, Prop(root, "link_token") ?? string.Empty, DateTime.UtcNow, ct).ConfigureAwait(false),
            _ => false,
        };

        if (!ok)
        {
            return Unauthorized();
        }

        await sessions.SetSudoAtAsync(sessionId, DateTime.UtcNow, ct).ConfigureAwait(false);
        await rateLimiter.ResetAccountAsync(user.EmailNormalized, ct).ConfigureAwait(false);
        return Results.Ok(new { sudo = true });
    }

    /// <summary>Returns assertion options + a correlation token for a passkey sudo step.</summary>
    private static async Task<IResult> SudoPasskeyOptionsAsync(
        HttpContext ctx, WebAuthnCeremony webAuthn, WebAuthnEnrollmentStore store, CancellationToken ct)
    {
        var userId = ctx.User.FindFirst(AuthClaims.UserId)?.Value;
        if (userId is null)
        {
            return Unauthorized();
        }

        if (!webAuthn.IsConfigured)
        {
            return Results.Json(new { error = "webauthn_unconfigured" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var optionsJson = await webAuthn.BeginAssertionAsync(userId, ct).ConfigureAwait(false);
        var correlation = store.Stash(userId, optionsJson);
        return Results.Content($"{{\"correlation\":\"{correlation}\",\"options\":{optionsJson}}}", "application/json");
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
        var userId = ctx.User.FindFirst(AuthClaims.UserId)?.Value;
        if (userId is null)
        {
            return Unauthorized();
        }

        var user = await users.GetByIdAsync(userId, ct).ConfigureAwait(false);
        var sender = sp.GetService<AuthEmailService>();
        if (user is null || sender is null)
        {
            return Results.Json(new { error = "magic_link_unavailable" }, statusCode: StatusCodes.Status400BadRequest);
        }

        // Gate on the user's actual factor set. A passkey/TOTP user is NOT offered magic-link.
        var available = await mfaFactors.AvailableAsync(user, ct).ConfigureAwait(false);
        if (!available.Contains(MfaFactors.MagicLink))
        {
            return Results.Json(new { error = "magic_link_unavailable" }, statusCode: StatusCodes.Status400BadRequest);
        }

        var limited = await rateLimiter.CheckAsync(user.EmailNormalized, ClientIp(ctx), ct).ConfigureAwait(false);
        if (limited.Limited)
        {
            return AuthRateLimiter.Throttled(limited);
        }

        var now = DateTime.UtcNow;

        // Find-or-create the single active sudo magic-link challenge AND record one send in one
        // atomic store call. Concurrent first sends converge on ONE row (a (user_id, sudo_dedupe_key)
        // unique key), so the per-challenge re-send cap cannot be multiplied by a race.
        var credential = await credentials.GetAsync(userId, ct).ConfigureAwait(false);
        var linkToken = tokenHasher.MintPrefixless();
        var result = await challenges.FindOrCreateSudoMagicLinkAsync(
            userId,
            credential?.CredentialVersion ?? 1,
            linkToken.Hash,
            linkToken.KeyVersion,
            now + options.Value.MfaChallengeLifetime,
            now + options.Value.MagicLinkLifetime,
            options.Value.MagicLinkMaxSends,
            now,
            ct).ConfigureAwait(false);
        if (!result.Sent)
        {
            // The per-challenge re-send cap was reached.
            return Results.Json(new { error = "send_cap_reached" }, statusCode: StatusCodes.Status429TooManyRequests);
        }

        await sender.SendMagicLinkAsync(user.Email, linkToken.Token, ct).ConfigureAwait(false);

        // The caller submits { factor: magic_link, challenge_id, link_token } to /auth/sudo.
        return Results.Ok(new { challenge_id = result.ChallengeId });
    }

    private static async Task<bool> VerifyPasswordAsync(
        IPasswordCredentialStore credentials, IPasswordHasher hasher, string userId, string? password, CancellationToken ct)
    {
        var credential = await credentials.GetAsync(userId, ct).ConfigureAwait(false);
        return credential is not null && hasher.Verify(password ?? string.Empty, credential.PasswordHash);
    }

    private static async Task<bool> VerifyPasskeyAsync(
        WebAuthnCeremony webAuthn, WebAuthnEnrollmentStore store, string userId, string? correlation, string? assertionJson, CancellationToken ct)
    {
        if (correlation is null || assertionJson is null)
        {
            return false;
        }

        var optionsJson = store.Take(userId, correlation);
        if (optionsJson is null)
        {
            return false;
        }

        try
        {
            return await webAuthn.VerifyAssertionAsync(userId, optionsJson, assertionJson, ct).ConfigureAwait(false);
        }
        catch (WebAuthnCeremonyException)
        {
            return false;
        }
    }

    private static string? Prop(System.Text.Json.JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;

    private static string? Payload(System.Text.Json.JsonElement root, string name)
        => root.TryGetProperty(name, out var v) ? v.GetRawText() : null;

    private static string? ClientIp(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString();

    private static IResult Unauthorized() => Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);
}
