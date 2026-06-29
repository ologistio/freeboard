using System.Text.Json.Serialization;
using Freeboard.Api;
using Freeboard.Persistence.Auth;
using Microsoft.Extensions.DependencyInjection;

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
        => await VerifyAsync(ctx, mfa, users, rateLimiter, body?.MfaToken, MfaFactors.Totp,
            challenge => totp.VerifyAsync(challenge.UserId, body?.Code ?? string.Empty, ct), ct).ConfigureAwait(false);

    public sealed record MfaTokenRequest([property: JsonPropertyName("mfa_token")] string? MfaToken);

    private static async Task<IResult> PasskeyOptionsAsync(
        MfaTokenRequest body, MfaChallengeService mfa, CancellationToken ct)
    {
        var challenge = await mfa.ResolveAsync(body?.MfaToken ?? string.Empty, ct).ConfigureAwait(false);
        if (challenge is null || challenge.WebAuthnOptions is null)
        {
            return GenericUnauthorized();
        }

        // Return the assertion options stashed on the challenge row at login (correlated).
        return Results.Content(challenge.WebAuthnOptions, "application/json");
    }

    public sealed record PasskeyVerifyRequest(
        [property: JsonPropertyName("mfa_token")] string? MfaToken, object? Assertion);

    private static async Task<IResult> PasskeyAsync(
        HttpContext ctx, MfaChallengeService mfa, WebAuthnCeremony webAuthn, IUserStore users,
        AuthRateLimiter rateLimiter, CancellationToken ct)
    {
        // The assertion is opaque WebAuthn JSON; read the raw body so it is passed through verbatim.
        var (mfaToken, assertionJson) = await ReadTokenAndPayloadAsync(ctx, "assertion", ct).ConfigureAwait(false);
        return await VerifyAsync(ctx, mfa, users, rateLimiter, mfaToken, MfaFactors.Passkey,
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
            }, ct).ConfigureAwait(false);
    }

    public sealed record RecoveryVerifyRequest(
        [property: JsonPropertyName("mfa_token")] string? MfaToken,
        [property: JsonPropertyName("recovery_code")] string? RecoveryCode);

    private static async Task<IResult> RecoveryAsync(
        RecoveryVerifyRequest body, HttpContext ctx, MfaChallengeService mfa, IRecoveryCodeStore recovery,
        IUserStore users, AuthRateLimiter rateLimiter, CancellationToken ct)
        => await VerifyAsync(ctx, mfa, users, rateLimiter, body?.MfaToken, MfaFactors.Recovery,
            challenge => recovery.ConsumeAsync(challenge.UserId, body?.RecoveryCode ?? string.Empty, ct), ct)
            .ConfigureAwait(false);

    private static async Task<IResult> MagicLinkSendAsync(
        MfaTokenRequest body, HttpContext ctx, MfaChallengeService mfa, IMfaChallengeStore challenges,
        ITokenHasher tokenHasher, IUserStore users, AuthRateLimiter rateLimiter, IServiceProvider sp,
        CancellationToken ct)
    {
        var challenge = await mfa.ResolveAsync(body?.MfaToken ?? string.Empty, ct).ConfigureAwait(false);
        if (challenge is null)
        {
            return GenericUnauthorized();
        }

        var sender = sp.GetService<AuthEmailService>();
        if (sender is null || !challenge.Factors.Split(',').Contains(MfaFactors.MagicLink))
        {
            return Results.Json(new { error = "magic_link_unavailable" }, statusCode: StatusCodes.Status400BadRequest);
        }

        var user = await users.GetByIdAsync(challenge.UserId, ct).ConfigureAwait(false);
        if (user is null)
        {
            return GenericUnauthorized();
        }

        var limited = await rateLimiter.CheckAsync(user.EmailNormalized, ClientIp(ctx), ct).ConfigureAwait(false);
        if (limited.Limited)
        {
            return AuthRateLimiter.Throttled(limited);
        }

        // Mint a PREFIXLESS single-use token; store its hash + own key version on the row, capped.
        var minted = tokenHasher.MintPrefixless();
        var stored = await challenges.SetMagicLinkAsync(
            challenge.Id, minted.Hash, minted.KeyVersion, DateTime.UtcNow + mfa.MagicLinkLifetime, mfa.MaxSends, ct)
            .ConfigureAwait(false);
        if (!stored)
        {
            return Results.Json(new { error = "send_cap_reached" }, statusCode: StatusCodes.Status429TooManyRequests);
        }

        await sender.SendMagicLinkAsync(user.Email, minted.Token, ct).ConfigureAwait(false);
        return Results.Ok(new { sent = true });
    }

    public sealed record MagicLinkVerifyRequest(
        [property: JsonPropertyName("mfa_token")] string? MfaToken,
        [property: JsonPropertyName("link_token")] string? LinkToken);

    private static async Task<IResult> MagicLinkVerifyAsync(
        MagicLinkVerifyRequest body, HttpContext ctx, MfaChallengeService mfa, IMfaChallengeStore challenges,
        IUserStore users, AuthRateLimiter rateLimiter, CancellationToken ct)
        => await VerifyAsync(ctx, mfa, users, rateLimiter, body?.MfaToken, MfaFactors.MagicLink,
            challenge => challenges.VerifyMagicLinkAsync(challenge.Id, body?.LinkToken ?? string.Empty, DateTime.UtcNow, ct), ct)
            .ConfigureAwait(false);

    // ---- shared verify flow ----

    private static async Task<IResult> VerifyAsync(
        HttpContext ctx,
        MfaChallengeService mfa,
        IUserStore users,
        AuthRateLimiter rateLimiter,
        string? mfaToken,
        string factor,
        Func<MfaChallengeRow, Task<bool>> verify,
        CancellationToken ct)
    {
        var challenge = await mfa.ResolveAsync(mfaToken ?? string.Empty, ct).ConfigureAwait(false);
        if (challenge is null)
        {
            return GenericUnauthorized();
        }

        // The factor must be one this challenge offers.
        if (!challenge.Factors.Split(',').Contains(factor))
        {
            return GenericUnauthorized();
        }

        var user = await users.GetByIdAsync(challenge.UserId, ct).ConfigureAwait(false);
        if (user is null || !user.Enabled)
        {
            return GenericUnauthorized();
        }

        var limited = await rateLimiter.CheckAsync(user.EmailNormalized, ClientIp(ctx), ct).ConfigureAwait(false);
        if (limited.Limited)
        {
            return AuthRateLimiter.Throttled(limited);
        }

        if (!await verify(challenge).ConfigureAwait(false))
        {
            // Atomically bump attempts; when the cap is reached the challenge auto-consumes.
            await mfa.RegisterFailureAsync(challenge.Id, ct).ConfigureAwait(false);
            return GenericUnauthorized();
        }

        var token = await mfa.CompleteAsync(challenge, ct).ConfigureAwait(false);
        if (token is null)
        {
            return GenericUnauthorized(); // lost the consume race.
        }

        await rateLimiter.ResetAccountAsync(user.EmailNormalized, ct).ConfigureAwait(false);
        return Results.Ok(new { user = ApiResponses.UserObject(user), token });
    }

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
}
