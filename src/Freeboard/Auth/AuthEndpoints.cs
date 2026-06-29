using System.Security.Claims;
using System.Text.Json.Serialization;
using Freeboard.Api;
using Freeboard.Persistence.Auth;
using Microsoft.Extensions.DependencyInjection;

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
        var email = body?.Email ?? string.Empty;
        var password = body?.Password ?? string.Empty;
        var accountKey = IUserStore.Normalize(email);
        var clientIp = ctx.Connection.RemoteIpAddress?.ToString();

        // Rate-limit BOTH buckets before any expensive work. The account bucket locks
        // unknown emails too, so a 429 is enumeration-safe.
        var limit = await rateLimiter.CheckAsync(accountKey, clientIp, ct).ConfigureAwait(false);
        if (limit.Limited)
        {
            return AuthRateLimiter.Throttled(limit);
        }

        // Uniform lookup shape for known/unknown/disabled: always one user lookup and one
        // credential lookup, and always exactly one Argon2 verify (real or decoy), so timing does
        // not reveal whether the account exists.
        var user = await users.GetByEmailAsync(email, ct).ConfigureAwait(false);
        var credential = await credentials
            .GetAsync(user?.Id ?? UniformLookupId, ct).ConfigureAwait(false);

        if (user is null || !user.Enabled || credential is null)
        {
            hasher.VerifyDecoy(password); // equivalent work, always false.
            return GenericUnauthorized();
        }

        if (!hasher.Verify(password, credential.PasswordHash))
        {
            return GenericUnauthorized();
        }

        // Capture the credential epoch we just VERIFIED. The session/challenge is stamped with
        // THIS value, not a current epoch re-read later, so a password change in the window is caught
        // by the bearer epoch check (non-MFA) or the completion re-check (MFA).
        var verifiedCredentialVersion = credential.CredentialVersion;

        // A fully-credentialed user with MFA enrolled gets a 202 challenge with a
        // body-only mfa_token and the available factor set. The mfa_token is never a session bearer.
        // Do NOT reset the account bucket here: the password step alone is not a successful auth, so
        // resetting it would let someone who knows the password wipe the per-account throttle and
        // brute-force the MFA factor. The reset happens only after MFA completion succeeds.
        if (user.MfaEnabled)
        {
            var (mfaToken, factors) = await mfa
                .BeginChallengeAsync(user, verifiedCredentialVersion, ct).ConfigureAwait(false);
            return Results.Json(
                new { mfa_required = true, mfa_token = mfaToken, factors },
                statusCode: StatusCodes.Status202Accepted);
        }

        // Non-MFA path: authentication has fully succeeded. Reset ONLY the account bucket; the IP
        // bucket persists.
        await rateLimiter.ResetAccountAsync(accountKey, ct).ConfigureAwait(false);

        var authState = user.ForcePasswordReset ? SessionAuthState.ForceResetLimited : SessionAuthState.Full;
        var (token, _) = await sessions
            .IssueAsync(user.Id, authState, verifiedCredentialVersion, ct).ConfigureAwait(false);

        // The opportunistic rehash runs AFTER the token is issued and is best-effort. A DB
        // error here must never turn a correct login into a 500; swallow it (the old hash keeps
        // verifying and the next login retries). The password is never logged.
        // Pass the VERIFIED hash + epoch so the store does a compare-and-swap. If a password
        // change/reset landed between the verify above and this rehash, the row no longer matches and
        // the update is a no-op, so the newer password is never clobbered with the old one.
        if (hasher.NeedsRehash(credential.PasswordHash))
        {
            try
            {
                await credentials.UpdateHashAsync(
                    user.Id, credential.PasswordHash, verifiedCredentialVersion,
                    hasher.Hash(password), CurrentSecretVersion(sp), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Best-effort upgrade only; ignore the failure.
            }
        }

        return Results.Ok(new { user = ApiResponses.UserObject(user), token });
    }

    private static async Task<IResult> Me(HttpContext ctx, IUserStore users, CancellationToken ct)
    {
        var userId = ctx.User.FindFirst(AuthClaims.UserId)?.Value;
        if (userId is null)
        {
            return GenericUnauthorized();
        }

        var user = await users.GetByIdAsync(userId, ct).ConfigureAwait(false);
        return user is null ? GenericUnauthorized() : Results.Ok(ApiResponses.UserObject(user));
    }

    private static async Task<IResult> LogoutAsync(HttpContext ctx, ISessionStore sessions, CancellationToken ct)
    {
        var sessionId = ctx.User.FindFirst(AuthClaims.SessionId)?.Value;
        if (sessionId is not null)
        {
            await sessions.DeleteAsync(sessionId, ct).ConfigureAwait(false);
        }

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
        var userId = ctx.User.FindFirst(AuthClaims.UserId)?.Value;
        var sessionId = ctx.User.FindFirst(AuthClaims.SessionId)?.Value;
        // userId + sessionId come from the bearer claims; both are required to proceed.
        if (userId is null || sessionId is null)
        {
            return GenericUnauthorized();
        }

        if (string.IsNullOrEmpty(body?.NewPassword))
        {
            return ApiResponses.ValidationProblem("new_password", "A new password is required.");
        }

        var credential = await credentials.GetAsync(userId, ct).ConfigureAwait(false);
        if (credential is null || !hasher.Verify(body.OldPassword ?? string.Empty, credential.PasswordHash))
        {
            // old_password is the proof-of-presence; a mismatch is a validation error.
            return ApiResponses.ValidationProblem("old_password", "The current password is incorrect.");
        }

        // One transaction: set the new hash, bump the credential epoch, revoke every OTHER
        // session, and stamp the kept session's epoch. Atomic so stale sessions cannot survive the
        // change and a racing login cannot keep a prior-epoch session alive.
        await credentials.UpdateHashAndRevokeSessionsAsync(
            userId, hasher.Hash(body.NewPassword), CurrentSecretVersion(sp),
            keepSessionId: sessionId, setForcePasswordReset: null, upgradeKeptSessionToFull: false, ct)
            .ConfigureAwait(false);
        return Results.Ok(new { password_changed = true });
    }

    public sealed record ForgotPasswordRequest(string? Email);

    private static async Task<IResult> ForgotPasswordAsync(
        ForgotPasswordRequest body,
        IUserStore users,
        IPasswordResetStore resets,
        Microsoft.Extensions.Options.IOptions<WebAuthOptions> options,
        IServiceProvider sp,
        CancellationToken ct)
    {
        // ALWAYS uniform 200, regardless of account existence (enumeration-safe). A real account
        // gets a keyed-hashed reset token emailed; an unknown one silently does nothing. The email
        // sender is an optional seam (transport is operator config); when absent or reset is
        // disabled, nothing is sent but the response is still a uniform 200. Startup fail-fast
        // (Program.cs) prevents enabling reset with no sender, so the runtime stays uniform.
        var email = body?.Email ?? string.Empty;
        var user = await users.GetByEmailAsync(email, ct).ConfigureAwait(false);
        var emailSender = sp.GetService<IAuthEmailSender>();
        if (user is not null && options.Value.PasswordResetEnabled && emailSender is not null)
        {
            var expiresAt = DateTime.UtcNow + options.Value.PasswordResetLifetime;
            var minted = await resets.CreateAsync(user.Id, expiresAt, ct).ConfigureAwait(false);
            await emailSender.SendPasswordResetAsync(user.Email, minted.Token, ct).ConfigureAwait(false);
        }

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
        if (string.IsNullOrEmpty(body?.NewPassword))
        {
            return ApiResponses.ValidationProblem("new_password", "A new password is required.");
        }

        var userId = await resets
            .ConsumeAsync(body.Token ?? string.Empty, DateTime.UtcNow, ct).ConfigureAwait(false);
        if (userId is null)
        {
            return ApiResponses.ValidationProblem("token", "The reset token is invalid or expired.");
        }

        // One transaction: set the new hash, bump the credential epoch, clear any force-reset flag
        // (the user just chose a new password, so do not make them change it again on next login),
        // and revoke ALL of the user's sessions.
        await credentials.UpdateHashAndRevokeSessionsAsync(
            userId, hasher.Hash(body.NewPassword), CurrentSecretVersion(sp),
            keepSessionId: null, setForcePasswordReset: false, upgradeKeptSessionToFull: false, ct)
            .ConfigureAwait(false);
        return Results.Ok(new { password_reset = true });
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
        var userId = ctx.User.FindFirst(AuthClaims.UserId)?.Value;
        var sessionId = ctx.User.FindFirst(AuthClaims.SessionId)?.Value;
        if (userId is null || sessionId is null)
        {
            return GenericUnauthorized();
        }

        // This endpoint completes a FORCED reset only. A normal full session must not use it
        // to change its password without old_password (that is /auth/password/change). Reject
        // unless BOTH (a) the session claim is force-reset-limited AND (b) the user row still has
        // force_password_reset = true (re-checked from the store, defending a stale claim).
        if (!IsForceResetLimited(ctx.User))
        {
            return Forbidden("This endpoint is only for completing a required password reset.");
        }

        var current = await users.GetByIdAsync(userId, ct).ConfigureAwait(false);
        if (current is null || !current.ForcePasswordReset)
        {
            return Forbidden("This endpoint is only for completing a required password reset.");
        }

        if (string.IsNullOrEmpty(body?.NewPassword))
        {
            return ApiResponses.ValidationProblem("new_password", "A new password is required.");
        }

        // One transaction: set the new hash, bump the credential epoch, clear force_password_reset,
        // revoke OTHER sessions, and upgrade THIS session to full while stamping its stored epoch to
        // the new value - so the just-upgraded session keeps working (token unchanged).
        await credentials.UpdateHashAndRevokeSessionsAsync(
            userId, hasher.Hash(body.NewPassword), CurrentSecretVersion(sp),
            keepSessionId: sessionId, setForcePasswordReset: false, upgradeKeptSessionToFull: true, ct)
            .ConfigureAwait(false);
        return Results.Ok(new { password_set = true });
    }

    private static async Task<IResult> GetSessionAsync(
        string id, HttpContext ctx, ISessionStore sessions, CancellationToken ct)
    {
        var session = await sessions.GetByIdAsync(id, ct).ConfigureAwait(false);
        // IDOR-safe: a non-owned (or absent) session is 404, not 403, so existence is not disclosed.
        // An expired session is treated as not present so reads reflect only live sessions.
        if (session is null || !CanActOn(ctx, session.UserId) || session.ExpiresAt <= DateTime.UtcNow)
        {
            return Results.NotFound();
        }

        return Results.Ok(SessionObject(session));
    }

    private static async Task<IResult> DeleteSessionAsync(
        string id, HttpContext ctx, ISessionStore sessions, CancellationToken ct)
    {
        var session = await sessions.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (session is null || !CanActOn(ctx, session.UserId))
        {
            return Results.NotFound();
        }

        await sessions.DeleteAsync(id, ct).ConfigureAwait(false);
        return Results.Ok(new { deleted = true });
    }

    private static async Task<IResult> ListUserSessionsAsync(
        string id, HttpContext ctx, ISessionStore sessions, CancellationToken ct)
    {
        if (!CanActOn(ctx, id))
        {
            return Results.NotFound();
        }

        // List only live sessions; expired rows are filtered out (bearer auth already
        // rejects expired tokens, this keeps the listing consistent).
        var now = DateTime.UtcNow;
        var rows = await sessions.ListByUserAsync(id, ct).ConfigureAwait(false);
        return Results.Ok(rows.Where(r => r.ExpiresAt > now).Select(SessionObject));
    }

    private static async Task<IResult> DeleteUserSessionsAsync(
        string id, HttpContext ctx, ISessionStore sessions, CancellationToken ct)
    {
        if (!CanActOn(ctx, id))
        {
            return Results.NotFound();
        }

        var removed = await sessions.DeleteAllForUserAsync(id, ct).ConfigureAwait(false);
        return Results.Ok(new { deleted = removed });
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
        // Validate the bootstrap secret FIRST, BEFORE any rate-limit / DB work. A
        // wrong/absent/unconfigured secret returns 401 having touched no DB.
        var configured = options.Value.BootstrapSecret;
        var presented = body?.BootstrapSecret ?? ctx.Request.Headers["X-Freeboard-Bootstrap-Secret"].ToString();
        if (!BootstrapSecretMatches(configured, presented))
        {
            return GenericUnauthorized();
        }

        // Only after the secret passes do we rate-limit (which may open DB work) - this throttles
        // a holder of the correct secret, not an attacker probing it.
        var clientIp = ctx.Connection.RemoteIpAddress?.ToString();
        var limit = await rateLimiter.CheckAsync("setup", clientIp, ct).ConfigureAwait(false);
        if (limit.Limited)
        {
            return AuthRateLimiter.Throttled(limit);
        }

        if (string.IsNullOrWhiteSpace(body?.Email) || string.IsNullOrWhiteSpace(body.Name)
            || string.IsNullOrEmpty(body.Password))
        {
            return ApiResponses.ValidationProblem(new Dictionary<string, string[]>
            {
                ["email"] = body is null || string.IsNullOrWhiteSpace(body.Email) ? ["An email is required."] : [],
                ["name"] = body is null || string.IsNullOrWhiteSpace(body.Name) ? ["A name is required."] : [],
                ["password"] = body is null || string.IsNullOrEmpty(body.Password) ? ["A password is required."] : [],
            });
        }

        var hash = hasher.Hash(body.Password);
        var admin = await users.TryBootstrapAdminAsync(
            new NewUser(body.Email, body.Name, GlobalRoles.Admin), hash, CurrentSecretVersion(sp), ct)
            .ConfigureAwait(false);

        // The marker already existed: a first admin exists, so setup is self-disabled (409).
        if (admin is null)
        {
            return Results.Conflict(new { error = "already_initialized" });
        }

        // A freshly bootstrapped admin's credential epoch is 1 (the DEFAULT); issue under it.
        var (token, _) = await sessions
            .IssueAsync(admin.Id, SessionAuthState.Full, 1, ct).ConfigureAwait(false);
        return Results.Json(
            new { user = ApiResponses.UserObject(admin), token },
            statusCode: StatusCodes.Status201Created);
    }

    // ---- helpers ----

    // A fixed, never-issued ULID placeholder so the unknown-user credential lookup has the same
    // shape (one indexed PK lookup) as the known-user path, keeping timing uniform.
    private const string UniformLookupId = "00000000000000000000000000";

    private static IResult GenericUnauthorized() => Results.Json(
        new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);

    private static IResult Forbidden(string detail) => Results.Json(
        new { error = "forbidden", detail }, statusCode: StatusCodes.Status403Forbidden);

    /// <summary>
    /// Constant-time bootstrap-secret check. Both the configured and presented secrets are
    /// SHA-256'd to fixed 32-byte digests before <c>FixedTimeEquals</c>, so the comparison never
    /// short-circuits on a length mismatch (no length oracle). An unconfigured/empty configured
    /// secret means setup is disabled and always returns false (uniform 401, no oracle).
    /// </summary>
    private static bool BootstrapSecretMatches(string? configured, string? presented)
    {
        if (string.IsNullOrEmpty(configured))
        {
            return false;
        }

        var configuredDigest = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(configured));
        var presentedDigest = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(presented ?? string.Empty));
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            configuredDigest, presentedDigest);
    }

    /// <summary>True when the session's auth-state claim is force-reset-limited.</summary>
    private static bool IsForceResetLimited(ClaimsPrincipal user)
    {
        var raw = user.FindFirst(AuthClaims.AuthState)?.Value;
        return int.TryParse(raw, System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture, out var state)
            && state == (int)SessionAuthState.ForceResetLimited;
    }

    private static int CurrentSecretVersion(IServiceProvider sp)
        => sp.GetRequiredService<AuthCryptoOptions>().CurrentPasswordSecretVersion;

    private static bool IsAdmin(ClaimsPrincipal user)
        => string.Equals(user.FindFirst(AuthClaims.Role)?.Value, GlobalRoles.Admin, StringComparison.Ordinal);

    /// <summary>An admin may act cross-user; a non-admin only on their own id.</summary>
    private static bool CanActOn(HttpContext ctx, string targetUserId)
        => IsAdmin(ctx.User)
            || string.Equals(ctx.User.FindFirst(AuthClaims.UserId)?.Value, targetUserId, StringComparison.Ordinal);

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
