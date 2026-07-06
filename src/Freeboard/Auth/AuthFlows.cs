using System.Security.Cryptography;
using System.Text;
using Freeboard.Api;
using Freeboard.Persistence.Auth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Freeboard.Auth;

/// <summary>
/// The auth flow bodies, extracted from the minimal-API endpoint delegates so the same logic
/// can be driven by both the HTTP endpoints and the Razor page handlers. Each method takes the
/// cross-cutting inputs as parameters (the client IP for per-IP rate limiting, the caller's
/// identity from the bearer claims, and any already-parsed opaque passkey/sudo payload) and
/// returns a typed result that carries the status-code distinctions the endpoints encode (login
/// 202 vs 200, bootstrap 201 vs 409, magic-link-send 400 vs 429). The endpoint delegate maps the
/// result back to the exact <c>IResult</c> it returned before; a page handler maps the same
/// result to a screen or redirect.
/// </summary>
internal static class AuthFlows
{
    // A fixed, never-issued ULID placeholder so the unknown-user credential lookup has the same
    // shape (one indexed PK lookup) as the known-user path, keeping timing uniform.
    private const string UniformLookupId = "00000000000000000000000000";

    // ---- login ----

    internal abstract record LoginResult
    {
        internal sealed record RateLimited(AuthRateLimitOutcome Outcome) : LoginResult;

        internal sealed record Unauthorized : LoginResult;

        /// <summary>Password verified, MFA enrolled: 202 with a body-only mfa_token.</summary>
        internal sealed record MfaRequired(string MfaToken, IReadOnlyList<string> Factors) : LoginResult;

        /// <summary>Full session issued: 200 with the user object and session token.</summary>
        internal sealed record Success(UserRow User, string Token) : LoginResult;
    }

    internal static async Task<LoginResult> LoginAsync(
        string? email,
        string? password,
        string? clientIp,
        IUserStore users,
        IPasswordCredentialStore credentials,
        IPasswordHasher hasher,
        AuthRateLimiter rateLimiter,
        SessionIssuer sessions,
        MfaChallengeService mfa,
        IServiceProvider sp,
        CancellationToken ct)
    {
        email ??= string.Empty;
        password ??= string.Empty;
        var accountKey = IUserStore.Normalize(email);

        // Rate-limit BOTH buckets before any expensive work. The account bucket locks
        // unknown emails too, so a 429 is enumeration-safe.
        var limit = await rateLimiter.CheckAsync(accountKey, clientIp, ct).ConfigureAwait(false);
        if (limit.Limited)
        {
            return new LoginResult.RateLimited(limit);
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
            return new LoginResult.Unauthorized();
        }

        if (!hasher.Verify(password, credential.PasswordHash))
        {
            return new LoginResult.Unauthorized();
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
            return new LoginResult.MfaRequired(mfaToken, factors);
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

        return new LoginResult.Success(user, token);
    }

    // ---- me ----

    internal static async Task<UserRow?> MeAsync(string? userId, IUserStore users, CancellationToken ct)
    {
        if (userId is null)
        {
            return null;
        }

        return await users.GetByIdAsync(userId, ct).ConfigureAwait(false);
    }

    // ---- logout ----

    internal static async Task LogoutAsync(string? sessionId, ISessionStore sessions, CancellationToken ct)
    {
        if (sessionId is not null)
        {
            await sessions.DeleteAsync(sessionId, ct).ConfigureAwait(false);
        }
    }

    // ---- password change/forgot/reset, forced-reset completion ----

    /// <summary>Outcome shared by the password-mutation flows.</summary>
    internal abstract record PasswordResult
    {
        internal sealed record Unauthorized : PasswordResult;

        internal sealed record Forbidden(string Detail) : PasswordResult;

        internal sealed record Invalid(string Field, string Message) : PasswordResult;

        internal sealed record Ok : PasswordResult;
    }

    internal static async Task<PasswordResult> ChangePasswordAsync(
        string? userId,
        string? sessionId,
        string? oldPassword,
        string? newPassword,
        IPasswordCredentialStore credentials,
        IPasswordHasher hasher,
        IServiceProvider sp,
        CancellationToken ct)
    {
        // userId + sessionId come from the bearer claims; both are required to proceed.
        if (userId is null || sessionId is null)
        {
            return new PasswordResult.Unauthorized();
        }

        if (string.IsNullOrEmpty(newPassword))
        {
            return new PasswordResult.Invalid("new_password", "A new password is required.");
        }

        var credential = await credentials.GetAsync(userId, ct).ConfigureAwait(false);
        if (credential is null || !hasher.Verify(oldPassword ?? string.Empty, credential.PasswordHash))
        {
            // old_password is the proof-of-presence; a mismatch is a validation error.
            return new PasswordResult.Invalid("old_password", "The current password is incorrect.");
        }

        // One transaction: set the new hash, bump the credential epoch, revoke every OTHER
        // session, and stamp the kept session's epoch. Atomic so stale sessions cannot survive the
        // change and a racing login cannot keep a prior-epoch session alive.
        await credentials.UpdateHashAndRevokeSessionsAsync(
            userId, hasher.Hash(newPassword), CurrentSecretVersion(sp),
            keepSessionId: sessionId, setForcePasswordReset: null, upgradeKeptSessionToFull: false, ct)
            .ConfigureAwait(false);
        return new PasswordResult.Ok();
    }

    internal static async Task ForgotPasswordAsync(
        string? email,
        IUserStore users,
        IPasswordResetStore resets,
        IOptions<WebAuthOptions> options,
        ILoggerFactory loggerFactory,
        IServiceProvider sp,
        CancellationToken ct)
    {
        // ALWAYS uniform 200, regardless of account existence (enumeration-safe). A real account
        // gets a keyed-hashed reset token emailed; an unknown one silently does nothing. The email
        // sender is an optional seam (transport is operator config); when absent or reset is
        // disabled, nothing is sent but the response is still a uniform 200. Startup fail-fast
        // (Program.cs) prevents enabling reset with no sender, so the runtime stays uniform.
        email ??= string.Empty;
        var user = await users.GetByEmailAsync(email, ct).ConfigureAwait(false);
        var emailService = sp.GetService<AuthEmailService>();
        if (user is not null && options.Value.PasswordResetEnabled && emailService is not null)
        {
            try
            {
                var expiresAt = DateTime.UtcNow + options.Value.PasswordResetLifetime;
                var minted = await resets.CreateAsync(user.Id, expiresAt, ct).ConfigureAwait(false);
                await emailService.SendPasswordResetAsync(user.Email, minted.Token, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Both the token mint and the send run only on the known-account branch, so a failure
                // in either must NOT escape as a 500: a known account that 500s while an unknown
                // account 200s is an enumeration oracle. Both stay inside this catch and fall through
                // to the same uniform 200. The reset token row may already exist; it expires unused.
                // The Warning is the observability path for a masked provisioning/delivery outage.
                //
                // Log the exception TYPE only, never the exception object or its Message: a sender or
                // wrapped SMTP exception could carry the reset token in its Message, and the token is a
                // credential that must never be logged at information level or above.
                loggerFactory.CreateLogger("Freeboard.Auth.ForgotPassword").LogWarning(
                    "Password-reset provisioning or delivery failed for {Recipient} ({Error}); returning the uniform 200.",
                    user.Email, ex.GetType().Name);
            }
        }
    }

    internal static async Task<PasswordResult> ResetPasswordAsync(
        string? token,
        string? newPassword,
        IPasswordResetStore resets,
        IPasswordCredentialStore credentials,
        IPasswordHasher hasher,
        IServiceProvider sp,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(newPassword))
        {
            return new PasswordResult.Invalid("new_password", "A new password is required.");
        }

        var userId = await resets
            .ConsumeAsync(token ?? string.Empty, DateTime.UtcNow, ct).ConfigureAwait(false);
        if (userId is null)
        {
            return new PasswordResult.Invalid("token", "The reset token is invalid or expired.");
        }

        // One transaction: set the new hash, bump the credential epoch, clear any force-reset flag
        // (the user just chose a new password, so do not make them change it again on next login),
        // and revoke ALL of the user's sessions.
        await credentials.UpdateHashAndRevokeSessionsAsync(
            userId, hasher.Hash(newPassword), CurrentSecretVersion(sp),
            keepSessionId: null, setForcePasswordReset: false, upgradeKeptSessionToFull: false, ct)
            .ConfigureAwait(false);
        return new PasswordResult.Ok();
    }

    internal static async Task<PasswordResult> AccountPasswordAsync(
        string? userId,
        string? sessionId,
        bool isForceResetLimited,
        string? newPassword,
        IUserStore users,
        IPasswordCredentialStore credentials,
        IPasswordHasher hasher,
        IServiceProvider sp,
        CancellationToken ct)
    {
        if (userId is null || sessionId is null)
        {
            return new PasswordResult.Unauthorized();
        }

        // This endpoint completes a FORCED reset only. A normal full session must not use it
        // to change its password without old_password (that is /auth/password/change). Reject
        // unless BOTH (a) the session claim is force-reset-limited AND (b) the user row still has
        // force_password_reset = true (re-checked from the store, defending a stale claim).
        if (!isForceResetLimited)
        {
            return new PasswordResult.Forbidden("This endpoint is only for completing a required password reset.");
        }

        var current = await users.GetByIdAsync(userId, ct).ConfigureAwait(false);
        if (current is null || !current.ForcePasswordReset)
        {
            return new PasswordResult.Forbidden("This endpoint is only for completing a required password reset.");
        }

        if (string.IsNullOrEmpty(newPassword))
        {
            return new PasswordResult.Invalid("new_password", "A new password is required.");
        }

        // One transaction: set the new hash, bump the credential epoch, clear force_password_reset,
        // revoke OTHER sessions, and upgrade THIS session to full while stamping its stored epoch to
        // the new value - so the just-upgraded session keeps working (token unchanged).
        await credentials.UpdateHashAndRevokeSessionsAsync(
            userId, hasher.Hash(newPassword), CurrentSecretVersion(sp),
            keepSessionId: sessionId, setForcePasswordReset: false, upgradeKeptSessionToFull: true, ct)
            .ConfigureAwait(false);
        return new PasswordResult.Ok();
    }

    // ---- sessions ----

    internal static async Task<SessionRow?> GetSessionAsync(
        string id, string? callerUserId, bool callerIsAdmin, ISessionStore sessions, CancellationToken ct)
    {
        var session = await sessions.GetByIdAsync(id, ct).ConfigureAwait(false);
        // IDOR-safe: a non-owned (or absent) session is 404, not 403, so existence is not disclosed.
        // An expired session is treated as not present so reads reflect only live sessions.
        if (session is null || !CanActOn(callerUserId, callerIsAdmin, session.UserId)
            || session.ExpiresAt <= DateTime.UtcNow)
        {
            return null;
        }

        return session;
    }

    /// <summary>True when the named session was found and deleted; false when it is not the caller's.</summary>
    internal static async Task<bool> DeleteSessionAsync(
        string id, string? callerUserId, bool callerIsAdmin, ISessionStore sessions, CancellationToken ct)
    {
        var session = await sessions.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (session is null || !CanActOn(callerUserId, callerIsAdmin, session.UserId))
        {
            return false;
        }

        await sessions.DeleteAsync(id, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Live sessions for the target user, or null when the caller may not act on it.</summary>
    internal static async Task<IReadOnlyList<SessionRow>?> ListUserSessionsAsync(
        string id, string? callerUserId, bool callerIsAdmin, ISessionStore sessions, CancellationToken ct)
    {
        if (!CanActOn(callerUserId, callerIsAdmin, id))
        {
            return null;
        }

        // List only live sessions; expired rows are filtered out (bearer auth already
        // rejects expired tokens, this keeps the listing consistent).
        var now = DateTime.UtcNow;
        var rows = await sessions.ListByUserAsync(id, ct).ConfigureAwait(false);
        return rows.Where(r => r.ExpiresAt > now).ToList();
    }

    /// <summary>The number of revoked sessions, or null when the caller may not act on the target.</summary>
    internal static async Task<int?> DeleteUserSessionsAsync(
        string id, string? callerUserId, bool callerIsAdmin, ISessionStore sessions, CancellationToken ct)
    {
        if (!CanActOn(callerUserId, callerIsAdmin, id))
        {
            return null;
        }

        return await sessions.DeleteAllForUserAsync(id, ct).ConfigureAwait(false);
    }

    // ---- bootstrap (first-admin setup) ----

    internal abstract record BootstrapResult
    {
        internal sealed record Unauthorized : BootstrapResult;

        internal sealed record RateLimited(AuthRateLimitOutcome Outcome) : BootstrapResult;

        internal sealed record Invalid(IDictionary<string, string[]> Errors) : BootstrapResult;

        /// <summary>The marker already existed: setup is self-disabled (409).</summary>
        internal sealed record AlreadyInitialized : BootstrapResult;

        /// <summary>First admin created: 201 with the user object and session token.</summary>
        internal sealed record Created(UserRow User, string Token) : BootstrapResult;
    }

    internal static async Task<BootstrapResult> BootstrapAsync(
        string? email,
        string? name,
        string? password,
        string? presentedSecret,
        string? clientIp,
        IUserStore users,
        IPasswordHasher hasher,
        AuthRateLimiter rateLimiter,
        SessionIssuer sessions,
        IOptions<WebAuthOptions> options,
        IServiceProvider sp,
        CancellationToken ct)
    {
        // Validate the bootstrap secret FIRST, BEFORE any rate-limit / DB work. A
        // wrong/absent/unconfigured secret returns 401 having touched no DB.
        if (!BootstrapSecretMatches(options.Value.BootstrapSecret, presentedSecret))
        {
            return new BootstrapResult.Unauthorized();
        }

        // Only after the secret passes do we rate-limit (which may open DB work) - this throttles
        // a holder of the correct secret, not an attacker probing it.
        var limit = await rateLimiter.CheckAsync("setup", clientIp, ct).ConfigureAwait(false);
        if (limit.Limited)
        {
            return new BootstrapResult.RateLimited(limit);
        }

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(name)
            || string.IsNullOrEmpty(password))
        {
            return new BootstrapResult.Invalid(new Dictionary<string, string[]>
            {
                ["email"] = string.IsNullOrWhiteSpace(email) ? ["An email is required."] : [],
                ["name"] = string.IsNullOrWhiteSpace(name) ? ["A name is required."] : [],
                ["password"] = string.IsNullOrEmpty(password) ? ["A password is required."] : [],
            });
        }

        var hash = hasher.Hash(password);
        var admin = await users.TryBootstrapAdminAsync(
            new NewUser(email, name, GlobalRoles.Admin), hash, CurrentSecretVersion(sp), ct)
            .ConfigureAwait(false);

        // The marker already existed: a first admin exists, so setup is self-disabled (409).
        if (admin is null)
        {
            return new BootstrapResult.AlreadyInitialized();
        }

        // A freshly bootstrapped admin's credential epoch is 1 (the DEFAULT); issue under it.
        var (token, _) = await sessions
            .IssueAsync(admin.Id, SessionAuthState.Full, 1, ct).ConfigureAwait(false);
        return new BootstrapResult.Created(admin, token);
    }

    // ---- admin user management (create / reset-password credential handoff) ----

    /// <summary>The credential handoff a create requests: a one-time temp password, or an emailed invite.</summary>
    internal enum CreateUserHandoff
    {
        TemporaryPassword,
        EmailInvite,
    }

    /// <summary>The expiry window for an emailed invite's set-password link: long enough to reach an
    /// inbox and be acted on, unlike the 1h public reset lifetime.</summary>
    internal static readonly TimeSpan InviteLifetime = TimeSpan.FromDays(7);

    internal abstract record CreateUserResult
    {
        /// <summary>Temp-password path: the created user plus the one-time plaintext to display once.</summary>
        internal sealed record Success(UserRow User, string TemporaryPassword) : CreateUserResult;

        /// <summary>Invite path: the row was created and a set-password link was emailed; no plaintext.</summary>
        internal sealed record Invited(UserRow User) : CreateUserResult;

        /// <summary>Invite path: the row was created, but minting the token or sending the email threw.</summary>
        internal sealed record InviteSendFailed(UserRow User) : CreateUserResult;

        /// <summary>Field validation failed (missing email/name, unknown role).</summary>
        internal sealed record Invalid(IDictionary<string, string[]> Errors) : CreateUserResult;

        /// <summary>The email is already taken (pre-check or the unique-key catch).</summary>
        internal sealed record DuplicateEmail : CreateUserResult;
    }

    internal static async Task<CreateUserResult> CreateUserAsync(
        string? email,
        string? name,
        string? globalRole,
        CreateUserHandoff handoff,
        IUserStore users,
        IPasswordCredentialStore credentials,
        IPasswordHasher hasher,
        IPasswordResetStore resets,
        IServiceProvider sp,
        CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(email))
        {
            errors["email"] = ["An email is required."];
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            errors["name"] = ["A name is required."];
        }

        var role = globalRole ?? GlobalRoles.Member;
        if (!GlobalRoles.IsValid(role))
        {
            errors["global_role"] = ["Unknown role."];
        }

        if (errors.Count > 0)
        {
            return new CreateUserResult.Invalid(errors);
        }

        // Pre-check gives a clean error in the common case; the DB unique index is still the
        // authoritative guard under a concurrent-create race (caught below).
        if (await users.GetByEmailAsync(email!, ct).ConfigureAwait(false) is not null)
        {
            return new CreateUserResult.DuplicateEmail();
        }

        // The invite path needs a configured email transport. Its presence (AuthEmailService) is the
        // single source of truth for "email is configured"; it is registered only when a sender is.
        // The gate is enforced here, not just in the page markup, so a forged invite request with no
        // email configured silently falls back to the temp-password path rather than failing.
        var emailService = sp.GetService<AuthEmailService>();
        var invite = handoff == CreateUserHandoff.EmailInvite && emailService is not null;
        var isAdmin = string.Equals(role, GlobalRoles.Admin, StringComparison.Ordinal);
        var newUser = new NewUser(email!, name!, role);
        var secretVersion = CurrentSecretVersion(sp);

        if (invite)
        {
            // The row is created with force_password_reset and NO password credential; until the invite
            // is accepted the account cannot log in. For an admin, the user row, the force-reset flag,
            // and the super-admin assignment commit as ONE unit (no credential yet), so no orphan
            // super-admin is left and the invited admin is not counted as a usable super-admin.
            UserRow invitedUser;
            try
            {
                if (isAdmin)
                {
                    invitedUser = await users.CreateAdminAsync(
                        newUser, passwordHash: null, secretVersion, forcePasswordReset: true, ct).ConfigureAwait(false);
                }
                else
                {
                    invitedUser = await users.CreateAsync(newUser, ct).ConfigureAwait(false);
                    await users.SetForcePasswordResetAsync(invitedUser.Id, true, ct).ConfigureAwait(false);
                }
            }
            catch (MySqlConnector.MySqlException ex) when (ex.ErrorCode == MySqlConnector.MySqlErrorCode.DuplicateKeyEntry)
            {
                return new CreateUserResult.DuplicateEmail();
            }

            var invited = invitedUser with { ForcePasswordReset = true };

            // Token mint + send are ONE invite-provisioning step: a throw from either leaves the row
            // present (recoverable via reset-password) and surfaces as InviteSendFailed, not a 500.
            // The invite deliberately does NOT honor Auth:PasswordResetEnabled: it is an authenticated
            // admin action, independent of the public self-serve forgot-password toggle.
            try
            {
                var minted = await resets.CreateAsync(invited.Id, DateTime.UtcNow + InviteLifetime, ct).ConfigureAwait(false);
                await emailService!.SendInviteAsync(invited.Email, minted.Token, InviteLifetime, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new CreateUserResult.InviteSendFailed(invited);
            }

            return new CreateUserResult.Invited(invited);
        }

        // Temp-password path: a random ONE-TIME temp password; store only its hash; force a reset. For
        // an admin, the user row, credential, force-reset flag, and super-admin assignment commit
        // atomically, so a failed credential write rolls the whole create back (no orphan super-admin).
        var tempPassword = TempPassword.Generate();
        UserRow user;
        try
        {
            if (isAdmin)
            {
                user = await users.CreateAdminAsync(
                    newUser, hasher.Hash(tempPassword), secretVersion, forcePasswordReset: true, ct).ConfigureAwait(false);
            }
            else
            {
                user = await users.CreateAsync(newUser, ct).ConfigureAwait(false);
                await credentials.SetAsync(user.Id, hasher.Hash(tempPassword), secretVersion, ct).ConfigureAwait(false);
                await users.SetForcePasswordResetAsync(user.Id, true, ct).ConfigureAwait(false);
            }
        }
        catch (MySqlConnector.MySqlException ex) when (ex.ErrorCode == MySqlConnector.MySqlErrorCode.DuplicateKeyEntry)
        {
            return new CreateUserResult.DuplicateEmail();
        }

        return new CreateUserResult.Success(user with { ForcePasswordReset = true }, tempPassword);
    }

    internal abstract record ResetUserPasswordResult
    {
        /// <summary>The one-time plaintext to display once; the user's sessions were revoked.</summary>
        internal sealed record Success(string TemporaryPassword) : ResetUserPasswordResult;

        /// <summary>The target id is unknown (a stale id deleted since the list was rendered).</summary>
        internal sealed record UnknownUser : ResetUserPasswordResult;
    }

    internal static async Task<ResetUserPasswordResult> ResetUserPasswordAsync(
        string id,
        IUserStore users,
        IPasswordCredentialStore credentials,
        IPasswordHasher hasher,
        IServiceProvider sp,
        CancellationToken ct)
    {
        var user = await users.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (user is null)
        {
            return new ResetUserPasswordResult.UnknownUser();
        }

        // One transaction: set the new hash, bump the credential epoch, force a password reset,
        // AND revoke ALL the user's sessions.
        var tempPassword = TempPassword.Generate();
        await credentials.UpdateHashAndRevokeSessionsAsync(
            id, hasher.Hash(tempPassword), CurrentSecretVersion(sp),
            keepSessionId: null, setForcePasswordReset: true, upgradeKeptSessionToFull: false, ct)
            .ConfigureAwait(false);

        return new ResetUserPasswordResult.Success(tempPassword);
    }

    // ---- MFA login verify ----

    internal abstract record MfaVerifyResult
    {
        internal sealed record Unauthorized : MfaVerifyResult;

        internal sealed record RateLimited(AuthRateLimitOutcome Outcome) : MfaVerifyResult;

        internal sealed record Success(UserRow User, string Token) : MfaVerifyResult;
    }

    internal static async Task<MfaVerifyResult> MfaVerifyAsync(
        string? mfaToken,
        string factor,
        string? clientIp,
        Func<MfaChallengeRow, Task<bool>> verify,
        MfaChallengeService mfa,
        IUserStore users,
        AuthRateLimiter rateLimiter,
        CancellationToken ct)
    {
        var challenge = await mfa.ResolveAsync(mfaToken ?? string.Empty, ct).ConfigureAwait(false);
        if (challenge is null)
        {
            return new MfaVerifyResult.Unauthorized();
        }

        // The factor must be one this challenge offers.
        if (!challenge.Factors.Split(',').Contains(factor))
        {
            return new MfaVerifyResult.Unauthorized();
        }

        var user = await users.GetByIdAsync(challenge.UserId, ct).ConfigureAwait(false);
        if (user is null || !user.Enabled)
        {
            return new MfaVerifyResult.Unauthorized();
        }

        var limited = await rateLimiter.CheckAsync(user.EmailNormalized, clientIp, ct).ConfigureAwait(false);
        if (limited.Limited)
        {
            return new MfaVerifyResult.RateLimited(limited);
        }

        if (!await verify(challenge).ConfigureAwait(false))
        {
            // Atomically bump attempts; when the cap is reached the challenge auto-consumes.
            await mfa.RegisterFailureAsync(challenge.Id, ct).ConfigureAwait(false);
            return new MfaVerifyResult.Unauthorized();
        }

        var token = await mfa.CompleteAsync(challenge, ct).ConfigureAwait(false);
        if (token is null)
        {
            return new MfaVerifyResult.Unauthorized(); // lost the consume race.
        }

        await rateLimiter.ResetAccountAsync(user.EmailNormalized, ct).ConfigureAwait(false);
        return new MfaVerifyResult.Success(user, token);
    }

    /// <summary>The cached assertion options for a login passkey challenge, or null when absent.</summary>
    internal static async Task<string?> MfaPasskeyOptionsAsync(
        string? mfaToken, MfaChallengeService mfa, CancellationToken ct)
    {
        var challenge = await mfa.ResolveAsync(mfaToken ?? string.Empty, ct).ConfigureAwait(false);
        return challenge?.WebAuthnOptions;
    }

    internal abstract record MagicLinkSendResult
    {
        internal sealed record Unauthorized : MagicLinkSendResult;

        /// <summary>Magic-link is not an available factor or no sender: 400.</summary>
        internal sealed record Unavailable : MagicLinkSendResult;

        internal sealed record RateLimited(AuthRateLimitOutcome Outcome) : MagicLinkSendResult;

        /// <summary>The per-challenge re-send cap was reached: 429.</summary>
        internal sealed record SendCapReached : MagicLinkSendResult;

        internal sealed record Sent : MagicLinkSendResult;
    }

    internal static async Task<MagicLinkSendResult> MagicLinkSendAsync(
        string? mfaToken,
        string? clientIp,
        MfaChallengeService mfa,
        IMfaChallengeStore challenges,
        ITokenHasher tokenHasher,
        IUserStore users,
        AuthRateLimiter rateLimiter,
        IServiceProvider sp,
        CancellationToken ct)
    {
        var challenge = await mfa.ResolveAsync(mfaToken ?? string.Empty, ct).ConfigureAwait(false);
        if (challenge is null)
        {
            return new MagicLinkSendResult.Unauthorized();
        }

        var sender = sp.GetService<AuthEmailService>();
        if (sender is null || !challenge.Factors.Split(',').Contains(MfaFactors.MagicLink))
        {
            return new MagicLinkSendResult.Unavailable();
        }

        var user = await users.GetByIdAsync(challenge.UserId, ct).ConfigureAwait(false);
        if (user is null)
        {
            return new MagicLinkSendResult.Unauthorized();
        }

        var limited = await rateLimiter.CheckAsync(user.EmailNormalized, clientIp, ct).ConfigureAwait(false);
        if (limited.Limited)
        {
            return new MagicLinkSendResult.RateLimited(limited);
        }

        // Mint a PREFIXLESS single-use token; store its hash + own key version on the row, capped.
        var minted = tokenHasher.MintPrefixless();
        var stored = await challenges.SetMagicLinkAsync(
            challenge.Id, minted.Hash, minted.KeyVersion, DateTime.UtcNow + mfa.MagicLinkLifetime, mfa.MaxSends, ct)
            .ConfigureAwait(false);
        if (!stored)
        {
            return new MagicLinkSendResult.SendCapReached();
        }

        await sender.SendMagicLinkAsync(user.Email, minted.Token, ct).ConfigureAwait(false);
        return new MagicLinkSendResult.Sent();
    }

    // ---- MFA enrollment ----

    internal sealed record MfaStatus(
        bool Totp, IReadOnlyList<WebAuthnCredentialRow> Passkeys, int RecoveryCodesRemaining);

    internal static async Task<MfaStatus?> MfaStatusAsync(
        string? userId, IWebAuthnCredentialStore webAuthn, ITotpStore totp, IRecoveryCodeStore recovery,
        CancellationToken ct)
    {
        if (userId is null)
        {
            return null;
        }

        var passkeys = await webAuthn.ListByUserAsync(userId, ct).ConfigureAwait(false);
        return new MfaStatus(
            await totp.IsConfirmedAsync(userId, ct).ConfigureAwait(false),
            passkeys,
            await recovery.CountRemainingAsync(userId, ct).ConfigureAwait(false));
    }

    /// <summary>The TOTP provisioning URI, or null when the caller is unauthenticated/unknown.</summary>
    internal static async Task<string?> TotpEnrollAsync(
        string? userId, ITotpStore totp, IUserStore users, CancellationToken ct)
    {
        if (userId is null)
        {
            return null;
        }

        var user = await users.GetByIdAsync(userId, ct).ConfigureAwait(false);
        if (user is null)
        {
            return null;
        }

        var enrollment = await totp.EnrollAsync(userId, user.Email, "Freeboard", ct).ConfigureAwait(false);
        return enrollment.ProvisioningUri;
    }

    /// <summary>Outcome of activating a factor (TOTP activate, passkey register).</summary>
    internal abstract record FactorActivationResult
    {
        internal sealed record Unauthorized : FactorActivationResult;

        internal sealed record Invalid(string Field, string Message) : FactorActivationResult;

        internal sealed record WebAuthnUnconfigured : FactorActivationResult;

        /// <summary><paramref name="RecoveryCodes"/> is non-null only on the FIRST factor.</summary>
        internal sealed record Activated(IReadOnlyList<string>? RecoveryCodes) : FactorActivationResult;
    }

    internal static async Task<FactorActivationResult> TotpActivateAsync(
        string? userId,
        string? code,
        ITotpStore totp,
        IWebAuthnCredentialStore webAuthn,
        IRecoveryCodeStore recovery,
        IUserStore users,
        IOptions<WebAuthOptions> options,
        CancellationToken ct)
    {
        if (userId is null)
        {
            return new FactorActivationResult.Unauthorized();
        }

        if (string.IsNullOrEmpty(code))
        {
            return new FactorActivationResult.Invalid("code", "A confirming code is required.");
        }

        var hadFactor = await HasStrongFactorAsync(webAuthn, totp, userId, ct).ConfigureAwait(false);
        if (!await totp.ActivateAsync(userId, code, ct).ConfigureAwait(false))
        {
            return new FactorActivationResult.Invalid("code", "The code is incorrect.");
        }

        var codes = await OnFactorActivatedAsync(users, recovery, userId, hadFactor, options.Value.RecoveryCodeCount, ct)
            .ConfigureAwait(false);
        return new FactorActivationResult.Activated(codes);
    }

    /// <summary>False when the caller is unauthenticated; true after the delete + flag recompute.</summary>
    internal static async Task<bool> TotpDeleteAsync(
        string? userId, ITotpStore totp, IWebAuthnCredentialStore webAuthn, IUserStore users, CancellationToken ct)
    {
        if (userId is null)
        {
            return false;
        }

        await totp.DeleteAsync(userId, ct).ConfigureAwait(false);
        await RecomputeMfaEnabledAsync(users, webAuthn, totp, userId, ct).ConfigureAwait(false);
        return true;
    }

    internal abstract record PasskeyRegisterOptionsResult
    {
        internal sealed record Unauthorized : PasskeyRegisterOptionsResult;

        internal sealed record WebAuthnUnconfigured : PasskeyRegisterOptionsResult;

        /// <summary>The combined correlation + raw options JSON for the page/endpoint to serialize.</summary>
        internal sealed record Ok(string Correlation, string OptionsJson) : PasskeyRegisterOptionsResult;
    }

    internal static async Task<PasskeyRegisterOptionsResult> PasskeyRegisterOptionsAsync(
        string? userId, WebAuthnCeremony webAuthn, WebAuthnEnrollmentStore store, IUserStore users, CancellationToken ct)
    {
        if (userId is null)
        {
            return new PasskeyRegisterOptionsResult.Unauthorized();
        }

        if (!webAuthn.IsConfigured)
        {
            return new PasskeyRegisterOptionsResult.WebAuthnUnconfigured();
        }

        var user = await users.GetByIdAsync(userId, ct).ConfigureAwait(false);
        if (user is null)
        {
            return new PasskeyRegisterOptionsResult.Unauthorized();
        }

        var optionsJson = await webAuthn.BeginRegistrationAsync(userId, user.Email, ct).ConfigureAwait(false);
        var correlation = store.Stash(userId, optionsJson);
        return new PasskeyRegisterOptionsResult.Ok(correlation, optionsJson);
    }

    internal static async Task<FactorActivationResult> PasskeyRegisterAsync(
        string? userId,
        string? correlation,
        string? attestation,
        string? nickname,
        WebAuthnCeremony webAuthn,
        WebAuthnEnrollmentStore store,
        IWebAuthnCredentialStore creds,
        ITotpStore totp,
        IRecoveryCodeStore recovery,
        IUserStore users,
        IOptions<WebAuthOptions> options,
        CancellationToken ct)
    {
        if (userId is null)
        {
            return new FactorActivationResult.Unauthorized();
        }

        if (!webAuthn.IsConfigured)
        {
            return new FactorActivationResult.WebAuthnUnconfigured();
        }

        if (correlation is null || attestation is null)
        {
            return new FactorActivationResult.Invalid("attestation", "A correlation and attestation are required.");
        }

        var optionsJson = store.Take(userId, correlation);
        if (optionsJson is null)
        {
            return new FactorActivationResult.Invalid("correlation", "The registration options expired. Restart enrollment.");
        }

        var hadFactor = await HasStrongFactorAsync(creds, totp, userId, ct).ConfigureAwait(false);
        try
        {
            await webAuthn.RegisterAsync(userId, optionsJson, attestation, nickname, ct).ConfigureAwait(false);
        }
        catch (WebAuthnCeremonyException)
        {
            return new FactorActivationResult.Invalid("attestation", "Passkey registration failed.");
        }

        var codes = await OnFactorActivatedAsync(users, recovery, userId, hadFactor, options.Value.RecoveryCodeCount, ct)
            .ConfigureAwait(false);
        return new FactorActivationResult.Activated(codes);
    }

    internal abstract record PasskeyDeleteResult
    {
        internal sealed record Unauthorized : PasskeyDeleteResult;

        internal sealed record NotFound : PasskeyDeleteResult;

        internal sealed record Ok : PasskeyDeleteResult;
    }

    internal static async Task<PasskeyDeleteResult> PasskeyDeleteAsync(
        string? userId, string id, IWebAuthnCredentialStore creds, ITotpStore totp, IUserStore users, CancellationToken ct)
    {
        if (userId is null)
        {
            return new PasskeyDeleteResult.Unauthorized();
        }

        // Only delete a credential the caller owns (IDOR-safe: 404 otherwise).
        var owned = (await creds.ListByUserAsync(userId, ct).ConfigureAwait(false)).Any(x => x.Id == id);
        if (!owned)
        {
            return new PasskeyDeleteResult.NotFound();
        }

        await creds.RemoveAsync(id, ct).ConfigureAwait(false);
        await RecomputeMfaEnabledAsync(users, creds, totp, userId, ct).ConfigureAwait(false);
        return new PasskeyDeleteResult.Ok();
    }

    /// <summary>The regenerated recovery codes, or null when the caller is unauthenticated.</summary>
    internal static async Task<IReadOnlyList<string>?> RecoveryRegenerateAsync(
        string? userId, IRecoveryCodeStore recovery, IOptions<WebAuthOptions> options, CancellationToken ct)
    {
        if (userId is null)
        {
            return null;
        }

        return await recovery.RegenerateAsync(userId, options.Value.RecoveryCodeCount, ct).ConfigureAwait(false);
    }

    // ---- sudo step-up ----

    internal abstract record SudoResult
    {
        internal sealed record Unauthorized : SudoResult;

        internal sealed record RateLimited(AuthRateLimitOutcome Outcome) : SudoResult;

        internal sealed record Ok : SudoResult;
    }

    internal static async Task<SudoResult> SudoAsync(
        string? userId,
        string? sessionId,
        string? clientIp,
        string? factor,
        string? password,
        string? code,
        string? recoveryCode,
        string? correlation,
        string? assertionJson,
        string? challengeId,
        string? linkToken,
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
        if (userId is null || sessionId is null)
        {
            return new SudoResult.Unauthorized();
        }

        var user = await users.GetByIdAsync(userId, ct).ConfigureAwait(false);
        if (user is null)
        {
            return new SudoResult.Unauthorized();
        }

        var limited = await rateLimiter.CheckAsync(user.EmailNormalized, clientIp, ct).ConfigureAwait(false);
        if (limited.Limited)
        {
            return new SudoResult.RateLimited(limited);
        }

        var ok = factor switch
        {
            // Non-MFA users (or an explicit password step) re-confirm the password.
            "password" or null when !user.MfaEnabled => await VerifyPasswordAsync(credentials, hasher, userId, password, ct).ConfigureAwait(false),
            MfaFactors.Totp => await totp.VerifyAsync(userId, code ?? string.Empty, ct).ConfigureAwait(false),
            MfaFactors.Recovery => await recovery.ConsumeAsync(userId, recoveryCode ?? string.Empty, ct).ConfigureAwait(false),
            MfaFactors.Passkey => await VerifyPasskeyAsync(webAuthn, enrollment, userId, correlation, assertionJson, ct).ConfigureAwait(false),
            // Atomic single-use consume, bound to the current bearer user.
            MfaFactors.MagicLink => await challenges.VerifyAndConsumeMagicLinkAsync(
                challengeId ?? string.Empty, userId, linkToken ?? string.Empty, DateTime.UtcNow, ct).ConfigureAwait(false),
            _ => false,
        };

        if (!ok)
        {
            return new SudoResult.Unauthorized();
        }

        await sessions.SetSudoAtAsync(sessionId, DateTime.UtcNow, ct).ConfigureAwait(false);
        await rateLimiter.ResetAccountAsync(user.EmailNormalized, ct).ConfigureAwait(false);
        return new SudoResult.Ok();
    }

    internal abstract record SudoPasskeyOptionsResult
    {
        internal sealed record Unauthorized : SudoPasskeyOptionsResult;

        internal sealed record WebAuthnUnconfigured : SudoPasskeyOptionsResult;

        internal sealed record Ok(string Correlation, string OptionsJson) : SudoPasskeyOptionsResult;
    }

    internal static async Task<SudoPasskeyOptionsResult> SudoPasskeyOptionsAsync(
        string? userId, WebAuthnCeremony webAuthn, WebAuthnEnrollmentStore store, CancellationToken ct)
    {
        if (userId is null)
        {
            return new SudoPasskeyOptionsResult.Unauthorized();
        }

        if (!webAuthn.IsConfigured)
        {
            return new SudoPasskeyOptionsResult.WebAuthnUnconfigured();
        }

        var optionsJson = await webAuthn.BeginAssertionAsync(userId, ct).ConfigureAwait(false);
        var correlation = store.Stash(userId, optionsJson);
        return new SudoPasskeyOptionsResult.Ok(correlation, optionsJson);
    }

    internal abstract record SudoMagicLinkSendResult
    {
        internal sealed record Unauthorized : SudoMagicLinkSendResult;

        /// <summary>No sender, no user, or magic-link is not an available factor: 400.</summary>
        internal sealed record Unavailable : SudoMagicLinkSendResult;

        internal sealed record RateLimited(AuthRateLimitOutcome Outcome) : SudoMagicLinkSendResult;

        /// <summary>The per-challenge re-send cap was reached: 429.</summary>
        internal sealed record SendCapReached : SudoMagicLinkSendResult;

        internal sealed record Sent(string ChallengeId) : SudoMagicLinkSendResult;
    }

    internal static async Task<SudoMagicLinkSendResult> SudoMagicLinkSendAsync(
        string? userId,
        string? clientIp,
        IUserStore users,
        IMfaChallengeStore challenges,
        IPasswordCredentialStore credentials,
        MfaFactorService mfaFactors,
        ITokenHasher tokenHasher,
        AuthRateLimiter rateLimiter,
        IOptions<WebAuthOptions> options,
        IServiceProvider sp,
        CancellationToken ct)
    {
        if (userId is null)
        {
            return new SudoMagicLinkSendResult.Unauthorized();
        }

        var user = await users.GetByIdAsync(userId, ct).ConfigureAwait(false);
        var sender = sp.GetService<AuthEmailService>();
        if (user is null || sender is null)
        {
            return new SudoMagicLinkSendResult.Unavailable();
        }

        // Gate on the user's actual factor set. A passkey/TOTP user is NOT offered magic-link.
        var available = await mfaFactors.AvailableAsync(user, ct).ConfigureAwait(false);
        if (!available.Contains(MfaFactors.MagicLink))
        {
            return new SudoMagicLinkSendResult.Unavailable();
        }

        var limited = await rateLimiter.CheckAsync(user.EmailNormalized, clientIp, ct).ConfigureAwait(false);
        if (limited.Limited)
        {
            return new SudoMagicLinkSendResult.RateLimited(limited);
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
            return new SudoMagicLinkSendResult.SendCapReached();
        }

        await sender.SendMagicLinkAsync(user.Email, linkToken.Token, ct).ConfigureAwait(false);
        return new SudoMagicLinkSendResult.Sent(result.ChallengeId);
    }

    // ---- shared helpers ----

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

    private static int CurrentSecretVersion(IServiceProvider sp)
        => sp.GetRequiredService<AuthCryptoOptions>().CurrentPasswordSecretVersion;

    /// <summary>An admin may act cross-user; a non-admin only on their own id.</summary>
    private static bool CanActOn(string? callerUserId, bool callerIsAdmin, string targetUserId)
        => callerIsAdmin || string.Equals(callerUserId, targetUserId, StringComparison.Ordinal);

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

        var configuredDigest = SHA256.HashData(Encoding.UTF8.GetBytes(configured));
        var presentedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(presented ?? string.Empty));
        return CryptographicOperations.FixedTimeEquals(configuredDigest, presentedDigest);
    }
}
