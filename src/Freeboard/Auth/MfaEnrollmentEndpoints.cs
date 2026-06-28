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
        var userId = UserId(ctx);
        if (userId is null)
        {
            return Unauthorized();
        }

        var passkeys = await webAuthn.ListByUserAsync(userId, ct).ConfigureAwait(false);
        return Results.Ok(new
        {
            totp = await totp.IsConfirmedAsync(userId, ct).ConfigureAwait(false),
            passkeys = passkeys.Select(c => new { id = c.Id, nickname = c.Nickname, created_at = c.CreatedAt }),
            recovery_codes_remaining = await recovery.CountRemainingAsync(userId, ct).ConfigureAwait(false),
        });
    }

    // ---- TOTP ----

    private static async Task<IResult> TotpEnrollAsync(HttpContext ctx, ITotpStore totp, IUserStore users, CancellationToken ct)
    {
        var userId = UserId(ctx);
        if (userId is null)
        {
            return Unauthorized();
        }

        var user = await users.GetByIdAsync(userId, ct).ConfigureAwait(false);
        if (user is null)
        {
            return Unauthorized();
        }

        var enrollment = await totp.EnrollAsync(userId, user.Email, "Freeboard", ct).ConfigureAwait(false);
        return Results.Ok(new { provisioning_uri = enrollment.ProvisioningUri });
    }

    public sealed record TotpActivateRequest(string? Code);

    private static async Task<IResult> TotpActivateAsync(
        TotpActivateRequest body, HttpContext ctx, ITotpStore totp, IWebAuthnCredentialStore webAuthn,
        IRecoveryCodeStore recovery, IUserStore users, IOptions<WebAuthOptions> options, CancellationToken ct)
    {
        var userId = UserId(ctx);
        if (userId is null)
        {
            return Unauthorized();
        }

        if (string.IsNullOrEmpty(body?.Code))
        {
            return ApiResponses.ValidationProblem("code", "A confirming code is required.");
        }

        var hadFactor = await HasStrongFactorAsync(webAuthn, totp, userId, ct).ConfigureAwait(false);
        if (!await totp.ActivateAsync(userId, body.Code, ct).ConfigureAwait(false))
        {
            return ApiResponses.ValidationProblem("code", "The code is incorrect.");
        }

        var codes = await OnFactorActivatedAsync(users, recovery, userId, hadFactor, options.Value.RecoveryCodeCount, ct)
            .ConfigureAwait(false);
        return Results.Ok(codes is null ? new { activated = true } : new { activated = true, recovery_codes = codes });
    }

    private static async Task<IResult> TotpDeleteAsync(
        HttpContext ctx, ITotpStore totp, IWebAuthnCredentialStore webAuthn, IUserStore users, CancellationToken ct)
    {
        var userId = UserId(ctx);
        if (userId is null)
        {
            return Unauthorized();
        }

        await totp.DeleteAsync(userId, ct).ConfigureAwait(false);
        await RecomputeMfaEnabledAsync(users, webAuthn, totp, userId, ct).ConfigureAwait(false);
        return Results.Ok(new { deleted = true });
    }

    // ---- passkey ----

    private static async Task<IResult> PasskeyOptionsAsync(
        HttpContext ctx, WebAuthnCeremony webAuthn, WebAuthnEnrollmentStore store, IUserStore users, CancellationToken ct)
    {
        var userId = UserId(ctx);
        if (userId is null)
        {
            return Unauthorized();
        }

        if (!webAuthn.IsConfigured)
        {
            return WebAuthnUnconfigured();
        }

        var user = await users.GetByIdAsync(userId, ct).ConfigureAwait(false);
        if (user is null)
        {
            return Unauthorized();
        }

        var optionsJson = await webAuthn.BeginRegistrationAsync(userId, user.Email, ct).ConfigureAwait(false);
        var correlation = store.Stash(userId, optionsJson);
        return Results.Content($"{{\"correlation\":\"{correlation}\",\"options\":{optionsJson}}}", "application/json");
    }

    private static async Task<IResult> PasskeyRegisterAsync(
        HttpContext ctx, WebAuthnCeremony webAuthn, WebAuthnEnrollmentStore store, IWebAuthnCredentialStore creds,
        ITotpStore totp, IRecoveryCodeStore recovery, IUserStore users, IOptions<WebAuthOptions> options,
        CancellationToken ct)
    {
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
        if (correlation is null || attestation is null)
        {
            return ApiResponses.ValidationProblem("attestation", "A correlation and attestation are required.");
        }

        var optionsJson = store.Take(userId, correlation);
        if (optionsJson is null)
        {
            return ApiResponses.ValidationProblem("correlation", "The registration options expired. Restart enrollment.");
        }

        var hadFactor = await HasStrongFactorAsync(creds, totp, userId, ct).ConfigureAwait(false);
        try
        {
            await webAuthn.RegisterAsync(userId, optionsJson, attestation, nickname, ct).ConfigureAwait(false);
        }
        catch (WebAuthnCeremonyException)
        {
            return ApiResponses.ValidationProblem("attestation", "Passkey registration failed.");
        }

        var codes = await OnFactorActivatedAsync(users, recovery, userId, hadFactor, options.Value.RecoveryCodeCount, ct)
            .ConfigureAwait(false);
        return Results.Ok(codes is null ? new { registered = true } : new { registered = true, recovery_codes = codes });
    }

    private static async Task<IResult> PasskeyDeleteAsync(
        string id, HttpContext ctx, IWebAuthnCredentialStore creds, ITotpStore totp, IUserStore users, CancellationToken ct)
    {
        var userId = UserId(ctx);
        if (userId is null)
        {
            return Unauthorized();
        }

        // Only delete a credential the caller owns (IDOR-safe: 404 otherwise).
        var owned = (await creds.ListByUserAsync(userId, ct).ConfigureAwait(false)).Any(x => x.Id == id);
        if (!owned)
        {
            return Results.NotFound();
        }

        await creds.RemoveAsync(id, ct).ConfigureAwait(false);
        await RecomputeMfaEnabledAsync(users, creds, totp, userId, ct).ConfigureAwait(false);
        return Results.Ok(new { deleted = true });
    }

    // ---- recovery ----

    private static async Task<IResult> RecoveryRegenerateAsync(
        HttpContext ctx, IRecoveryCodeStore recovery, IOptions<WebAuthOptions> options, CancellationToken ct)
    {
        var userId = UserId(ctx);
        if (userId is null)
        {
            return Unauthorized();
        }

        var codes = await recovery.RegenerateAsync(userId, options.Value.RecoveryCodeCount, ct).ConfigureAwait(false);
        return Results.Ok(new { recovery_codes = codes });
    }

    // ---- helpers ----

    private static string? UserId(HttpContext ctx) => ctx.User.FindFirst(AuthClaims.UserId)?.Value;

    private static IResult Unauthorized() => Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);

    private static IResult WebAuthnUnconfigured() => Results.Json(
        new { error = "webauthn_unconfigured" }, statusCode: StatusCodes.Status503ServiceUnavailable);

    private static async Task<bool> HasStrongFactorAsync(
        IWebAuthnCredentialStore webAuthn, ITotpStore totp, string userId, CancellationToken ct)
        => (await webAuthn.ListByUserAsync(userId, ct).ConfigureAwait(false)).Count > 0
            || await totp.IsConfirmedAsync(userId, ct).ConfigureAwait(false);

    /// <summary>
    /// On a factor activation: ensure mfa_enabled is true and, if this is the FIRST factor, generate
    /// the recovery-code set once and return it. Returns null when not the first factor.
    /// </summary>
    private static async Task<IReadOnlyList<string>?> OnFactorActivatedAsync(
        IUserStore users, IRecoveryCodeStore recovery, string userId, bool hadFactor, int count, CancellationToken ct)
    {
        await users.SetMfaEnabledAsync(userId, true, ct).ConfigureAwait(false);
        if (hadFactor)
        {
            return null;
        }

        return await recovery.RegenerateAsync(userId, count, ct).ConfigureAwait(false);
    }

    private static async Task RecomputeMfaEnabledAsync(
        IUserStore users, IWebAuthnCredentialStore webAuthn, ITotpStore totp, string userId, CancellationToken ct)
    {
        var stillHas = await HasStrongFactorAsync(webAuthn, totp, userId, ct).ConfigureAwait(false);
        await users.SetMfaEnabledAsync(userId, stillHas, ct).ConfigureAwait(false);
    }
}
