using System.Text.Json.Serialization;
using Freeboard.Api;
using Freeboard.Persistence.Auth;
using Microsoft.Extensions.Options;

namespace Freeboard.Auth;

/// <summary>
/// MFA enrollment endpoints, all under <see cref="ApiRoutes.ApiRoutePrefix"/>,
/// bearer-authenticated. Every STATE-CHANGING endpoint requires sudo-mode; the status read
/// does not. Activating the FIRST factor flips <c>users.mfa_enabled</c> and returns the recovery
/// codes ONCE; removing the last strong factor recomputes the flag.
/// </summary>
public static class MfaEnrollmentEndpoints
{
    public static void MapMfaEnrollmentEndpoints(this WebApplication app)
    {
        var g = app.MapGroup(ApiRoutes.ApiRoutePrefix);

        g.MapGet("/auth/mfa", StatusAsync).RequireAuthorization().MarkAuthEndpoint();

        g.MapPost("/auth/mfa/totp/enroll", TotpEnrollAsync).RequireAuthorization().RequireSudoMode().MarkAuthEndpoint();
        g.MapPost("/auth/mfa/totp/activate", TotpActivateAsync).RequireAuthorization().RequireSudoMode().MarkAuthEndpoint();
        g.MapDelete("/auth/mfa/totp", TotpDeleteAsync).RequireAuthorization().RequireSudoMode().MarkAuthEndpoint();

        // Enrollment passkey routes are distinct from the login-assertion routes (/auth/mfa/passkey,
        // /auth/mfa/passkey/options) to avoid a route collision: those are the unauthenticated login
        // verify; these are the bearer + sudo registration ceremony.
        g.MapPost("/auth/mfa/passkey/register-options", PasskeyOptionsAsync).RequireAuthorization().RequireSudoMode().MarkAuthEndpoint();
        g.MapPost("/auth/mfa/passkey/register", PasskeyRegisterAsync).RequireAuthorization().RequireSudoMode().MarkAuthEndpoint();
        g.MapDelete("/auth/mfa/passkey/{id}", PasskeyDeleteAsync).RequireAuthorization().RequireSudoMode().MarkAuthEndpoint();

        g.MapPost("/auth/mfa/recovery/regenerate", RecoveryRegenerateAsync).RequireAuthorization().RequireSudoMode().MarkAuthEndpoint();
    }

    private static async Task<IResult> StatusAsync(
        HttpContext ctx, IWebAuthnCredentialStore webAuthn, ITotpStore totp, IRecoveryCodeStore recovery,
        CancellationToken ct)
    {
        var status = await AuthFlows.MfaStatusAsync(UserId(ctx), webAuthn, totp, recovery, ct).ConfigureAwait(false);
        if (status is null)
        {
            return Unauthorized();
        }

        return Results.Ok(new
        {
            totp = status.Totp,
            passkeys = status.Passkeys.Select(c => new { id = c.Id, nickname = c.Nickname, created_at = c.CreatedAt }),
            recovery_codes_remaining = status.RecoveryCodesRemaining,
        });
    }

    #region TOTP
    private static async Task<IResult> TotpEnrollAsync(HttpContext ctx, ITotpStore totp, IUserStore users, CancellationToken ct)
    {
        var provisioningUri = await AuthFlows.TotpEnrollAsync(UserId(ctx), totp, users, ct).ConfigureAwait(false);
        return provisioningUri is null ? Unauthorized() : Results.Ok(new { provisioning_uri = provisioningUri });
    }

    public sealed record TotpActivateRequest(string? Code);

    private static async Task<IResult> TotpActivateAsync(
        TotpActivateRequest body, HttpContext ctx, ITotpStore totp, IWebAuthnCredentialStore webAuthn,
        IRecoveryCodeStore recovery, IUserStore users, IOptions<WebAuthOptions> options, CancellationToken ct)
        => MapActivated(
            await AuthFlows.TotpActivateAsync(
                UserId(ctx), body?.Code, totp, webAuthn, recovery, users, options, ct).ConfigureAwait(false),
            codes => codes is null ? new { activated = true } : new { activated = true, recovery_codes = codes });

    private static async Task<IResult> TotpDeleteAsync(
        HttpContext ctx, ITotpStore totp, IWebAuthnCredentialStore webAuthn, IUserStore users, CancellationToken ct)
    {
        var ok = await AuthFlows.TotpDeleteAsync(UserId(ctx), totp, webAuthn, users, ct).ConfigureAwait(false);
        return ok ? Results.Ok(new { deleted = true }) : Unauthorized();
    }

    #endregion

    #region passkey
    private static async Task<IResult> PasskeyOptionsAsync(
        HttpContext ctx, WebAuthnCeremony webAuthn, WebAuthnEnrollmentStore store, IUserStore users, CancellationToken ct)
    {
        var result = await AuthFlows.PasskeyRegisterOptionsAsync(UserId(ctx), webAuthn, store, users, ct)
            .ConfigureAwait(false);
        return result switch
        {
            AuthFlows.PasskeyRegisterOptionsResult.Unauthorized => Unauthorized(),
            AuthFlows.PasskeyRegisterOptionsResult.WebAuthnUnconfigured => WebAuthnUnconfigured(),
            AuthFlows.PasskeyRegisterOptionsResult.Ok ok => Results.Content(
                $"{{\"correlation\":\"{ok.Correlation}\",\"options\":{ok.OptionsJson}}}", "application/json"),
            _ => Unauthorized(),
        };
    }

    private static async Task<IResult> PasskeyRegisterAsync(
        HttpContext ctx, WebAuthnCeremony webAuthn, WebAuthnEnrollmentStore store, IWebAuthnCredentialStore creds,
        ITotpStore totp, IRecoveryCodeStore recovery, IUserStore users, IOptions<WebAuthOptions> options,
        CancellationToken ct)
    {
        // Keep the original ordering: reject unauthenticated/unconfigured BEFORE touching the body,
        // so a malformed body cannot mask a 401/503. The shared flow re-checks both for the page path.
        var userId = UserId(ctx);
        if (userId is null)
        {
            return Unauthorized();
        }

        if (!webAuthn.IsConfigured)
        {
            return WebAuthnUnconfigured();
        }

        using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;
        var correlation = root.TryGetProperty("correlation", out var c) ? c.GetString() : null;
        var attestation = root.TryGetProperty("attestation", out var a) ? a.GetRawText() : null;
        var nickname = root.TryGetProperty("nickname", out var n) ? n.GetString() : null;

        return MapActivated(
            await AuthFlows.PasskeyRegisterAsync(
                userId, correlation, attestation, nickname,
                webAuthn, store, creds, totp, recovery, users, options, ct).ConfigureAwait(false),
            codes => codes is null ? new { registered = true } : new { registered = true, recovery_codes = codes });
    }

    private static async Task<IResult> PasskeyDeleteAsync(
        string id, HttpContext ctx, IWebAuthnCredentialStore creds, ITotpStore totp, IUserStore users, CancellationToken ct)
    {
        var result = await AuthFlows.PasskeyDeleteAsync(UserId(ctx), id, creds, totp, users, ct).ConfigureAwait(false);
        return result switch
        {
            AuthFlows.PasskeyDeleteResult.Unauthorized => Unauthorized(),
            AuthFlows.PasskeyDeleteResult.NotFound => Results.NotFound(),
            AuthFlows.PasskeyDeleteResult.Ok => Results.Ok(new { deleted = true }),
            _ => Unauthorized(),
        };
    }

    #endregion

    #region recovery
    private static async Task<IResult> RecoveryRegenerateAsync(
        HttpContext ctx, IRecoveryCodeStore recovery, IOptions<WebAuthOptions> options, CancellationToken ct)
    {
        var codes = await AuthFlows.RecoveryRegenerateAsync(UserId(ctx), recovery, options, ct).ConfigureAwait(false);
        return codes is null ? Unauthorized() : Results.Ok(new { recovery_codes = codes });
    }

    #endregion

    #region helpers
    private static string? UserId(HttpContext ctx) => ctx.User.FindFirst(AuthClaims.UserId)?.Value;

    private static IResult Unauthorized() => Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);

    private static IResult WebAuthnUnconfigured() => Results.Json(
        new { error = "webauthn_unconfigured" }, statusCode: StatusCodes.Status503ServiceUnavailable);

    /// <summary>Maps a factor-activation outcome (shared by TOTP activate and passkey register) to its IResult.</summary>
    private static IResult MapActivated(
        AuthFlows.FactorActivationResult result, Func<IReadOnlyList<string>?, object> okBody)
        => result switch
        {
            AuthFlows.FactorActivationResult.Unauthorized => Unauthorized(),
            AuthFlows.FactorActivationResult.WebAuthnUnconfigured => WebAuthnUnconfigured(),
            AuthFlows.FactorActivationResult.Invalid v => ApiResponses.ValidationProblem(v.Field, v.Message),
            AuthFlows.FactorActivationResult.Activated a => Results.Ok(okBody(a.RecoveryCodes)),
            _ => Unauthorized(),
        };

    #endregion
}
