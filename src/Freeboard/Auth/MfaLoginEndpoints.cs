using System.Text.Json.Serialization;
using Freeboard.Api;
using Freeboard.Persistence.Auth;

namespace Freeboard.Auth;

/// <summary>
/// MFA login verify endpoints, all under <see cref="ApiRoutes.ApiRoutePrefix"/>
/// and tagged with <see cref="AuthEndpoint"/>. Each reads the body-only <c>mfa_token</c>, is
/// rate-limited, consumes the challenge atomically on success, enforces the 5-attempt cap, and on
/// success issues a FULL session returning <c>{ user, token }</c>. The mfa_token is never a bearer.
/// </summary>
public static class MfaLoginEndpoints
{
    public static void MapMfaLoginEndpoints(this WebApplication app)
    {
        var g = app.MapGroup(ApiRoutes.ApiRoutePrefix);

        g.MapPost("/auth/mfa/totp", TotpAsync).MarkAuthEndpoint();
        g.MapPost("/auth/mfa/passkey/options", PasskeyOptionsAsync).MarkAuthEndpoint();
        g.MapPost("/auth/mfa/passkey", PasskeyAsync).MarkAuthEndpoint();
        g.MapPost("/auth/mfa/recovery", RecoveryAsync).MarkAuthEndpoint();
        g.MapPost("/auth/mfa/magic-link/send", MagicLinkSendAsync).MarkAuthEndpoint();
        g.MapPost("/auth/mfa/magic-link/verify", MagicLinkVerifyAsync).MarkAuthEndpoint();
    }

    public sealed record TotpVerifyRequest(
        [property: JsonPropertyName("mfa_token")] string? MfaToken, string? Code);

    private static async Task<IResult> TotpAsync(
        TotpVerifyRequest body, HttpContext ctx, MfaChallengeService mfa, ITotpStore totp,
        IUserStore users, AuthRateLimiter rateLimiter, CancellationToken ct)
        => MapVerify(await AuthFlows.MfaVerifyAsync(
            body?.MfaToken, MfaFactors.Totp, ClientIp(ctx),
            challenge => totp.VerifyAsync(challenge.UserId, body?.Code ?? string.Empty, ct),
            mfa, users, rateLimiter, ct).ConfigureAwait(false));

    public sealed record MfaTokenRequest([property: JsonPropertyName("mfa_token")] string? MfaToken);

    private static async Task<IResult> PasskeyOptionsAsync(
        MfaTokenRequest body, MfaChallengeService mfa, CancellationToken ct)
    {
        var optionsJson = await AuthFlows.MfaPasskeyOptionsAsync(body?.MfaToken, mfa, ct).ConfigureAwait(false);
        // Return the assertion options stashed on the challenge row at login (correlated).
        return optionsJson is null ? GenericUnauthorized() : Results.Content(optionsJson, "application/json");
    }

    public sealed record PasskeyVerifyRequest(
        [property: JsonPropertyName("mfa_token")] string? MfaToken, object? Assertion);

    private static async Task<IResult> PasskeyAsync(
        HttpContext ctx, MfaChallengeService mfa, WebAuthnCeremony webAuthn, IUserStore users,
        AuthRateLimiter rateLimiter, CancellationToken ct)
    {
        // The assertion is opaque WebAuthn JSON; read the raw body so it is passed through verbatim.
        var (mfaToken, assertionJson) = await ReadTokenAndPayloadAsync(ctx, "assertion", ct).ConfigureAwait(false);
        return MapVerify(await AuthFlows.MfaVerifyAsync(
            mfaToken, MfaFactors.Passkey, ClientIp(ctx),
            async challenge =>
            {
                if (challenge.WebAuthnOptions is null || assertionJson is null)
                {
                    return false;
                }

                try
                {
                    return await webAuthn
                        .VerifyAssertionAsync(challenge.UserId, challenge.WebAuthnOptions, assertionJson, ct)
                        .ConfigureAwait(false);
                }
                catch (WebAuthnCeremonyException)
                {
                    return false; // mismatched origin/RP-id or invalid assertion -> failed factor.
                }
            },
            mfa, users, rateLimiter, ct).ConfigureAwait(false));
    }

    public sealed record RecoveryVerifyRequest(
        [property: JsonPropertyName("mfa_token")] string? MfaToken,
        [property: JsonPropertyName("recovery_code")] string? RecoveryCode);

    private static async Task<IResult> RecoveryAsync(
        RecoveryVerifyRequest body, HttpContext ctx, MfaChallengeService mfa, IRecoveryCodeStore recovery,
        IUserStore users, AuthRateLimiter rateLimiter, CancellationToken ct)
        => MapVerify(await AuthFlows.MfaVerifyAsync(
            body?.MfaToken, MfaFactors.Recovery, ClientIp(ctx),
            challenge => recovery.ConsumeAsync(challenge.UserId, body?.RecoveryCode ?? string.Empty, ct),
            mfa, users, rateLimiter, ct).ConfigureAwait(false));

    private static async Task<IResult> MagicLinkSendAsync(
        MfaTokenRequest body, HttpContext ctx, MfaChallengeService mfa, IMfaChallengeStore challenges,
        ITokenHasher tokenHasher, IUserStore users, AuthRateLimiter rateLimiter, IServiceProvider sp,
        CancellationToken ct)
    {
        var result = await AuthFlows.MagicLinkSendAsync(
            body?.MfaToken, ClientIp(ctx), mfa, challenges, tokenHasher, users, rateLimiter, sp, ct)
            .ConfigureAwait(false);
        return result switch
        {
            AuthFlows.MagicLinkSendResult.Unauthorized => GenericUnauthorized(),
            AuthFlows.MagicLinkSendResult.Unavailable => Results.Json(
                new { error = "magic_link_unavailable" }, statusCode: StatusCodes.Status400BadRequest),
            AuthFlows.MagicLinkSendResult.RateLimited r => AuthRateLimiter.Throttled(r.Outcome),
            AuthFlows.MagicLinkSendResult.SendCapReached => Results.Json(
                new { error = "send_cap_reached" }, statusCode: StatusCodes.Status429TooManyRequests),
            AuthFlows.MagicLinkSendResult.Sent => Results.Ok(new { sent = true }),
            _ => GenericUnauthorized(),
        };
    }

    public sealed record MagicLinkVerifyRequest(
        [property: JsonPropertyName("mfa_token")] string? MfaToken,
        [property: JsonPropertyName("link_token")] string? LinkToken);

    private static async Task<IResult> MagicLinkVerifyAsync(
        MagicLinkVerifyRequest body, HttpContext ctx, MfaChallengeService mfa, IMfaChallengeStore challenges,
        IUserStore users, AuthRateLimiter rateLimiter, CancellationToken ct)
        => MapVerify(await AuthFlows.MfaVerifyAsync(
            body?.MfaToken, MfaFactors.MagicLink, ClientIp(ctx),
            challenge => challenges.VerifyMagicLinkAsync(challenge.Id, body?.LinkToken ?? string.Empty, DateTime.UtcNow, ct),
            mfa, users, rateLimiter, ct).ConfigureAwait(false));

    #region result mapping
    private static IResult MapVerify(AuthFlows.MfaVerifyResult result) => result switch
    {
        AuthFlows.MfaVerifyResult.RateLimited r => AuthRateLimiter.Throttled(r.Outcome),
        AuthFlows.MfaVerifyResult.Success s => Results.Ok(new { user = ApiResponses.UserObject(s.User), token = s.Token }),
        _ => GenericUnauthorized(),
    };

    private static string? ClientIp(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString();

    private static IResult GenericUnauthorized() => Results.Json(
        new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);

    /// <summary>Reads a JSON body that carries an <c>mfa_token</c> string and an opaque payload object passed through verbatim.</summary>
    private static async Task<(string? MfaToken, string? Payload)> ReadTokenAndPayloadAsync(
        HttpContext ctx, string payloadProperty, CancellationToken ct)
    {
        using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct)
            .ConfigureAwait(false);
        var root = doc.RootElement;
        var mfaToken = root.TryGetProperty("mfa_token", out var t) ? t.GetString() : null;
        var payload = root.TryGetProperty(payloadProperty, out var p) ? p.GetRawText() : null;
        return (mfaToken, payload);
    }

    #endregion
}
