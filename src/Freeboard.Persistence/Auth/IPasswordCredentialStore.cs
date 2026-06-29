namespace Freeboard.Persistence.Auth;

/// <summary>
/// Password credential store: the Argon2id PHC hash plus its keyed-secret version,
/// kept out of the user profile. Hand-written SQL via Dapper.
/// </summary>
public interface IPasswordCredentialStore
{
    /// <summary>Inserts or replaces the credential for a user (used on create and on reset).</summary>
    Task SetAsync(string userId, string passwordHash, int secretVersion, CancellationToken cancellationToken = default);

    Task<PasswordCredentialRow?> GetAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Compare-and-swap rehash: updates the hash and secret version in place WITHOUT bumping
    /// the credential epoch (the opportunistic login rehash: same password, new parameters, so prior
    /// sessions stay valid). The update is conditional on the stored row STILL matching the exact hash
    /// that was verified AND the credential epoch that was verified, so a concurrent password
    /// change/reset that lands between verify and rehash is not clobbered with the old-password hash.
    /// Returns true when the row was updated, false when it changed underneath (no-op).
    /// </summary>
    Task<bool> UpdateHashAsync(
        string userId,
        string verifiedHash,
        int verifiedCredentialVersion,
        string newPasswordHash,
        int secretVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically sets a NEW password (bumping the credential epoch), optionally flips
    /// <c>force_password_reset</c> and/or upgrades the kept session to full, and revokes sessions -
    /// all in ONE transaction, so a partial failure cannot leave the new hash stored while
    /// stale sessions survive, and a concurrent login cannot keep a prior-epoch session alive.
    /// <list type="bullet">
    /// <item><paramref name="keepSessionId"/> = the current session id revokes every OTHER session
    /// and stamps the kept session's stored credential epoch to the new value (so it keeps working).</item>
    /// <item><paramref name="keepSessionId"/> = null revokes ALL sessions (password reset, admin reset).</item>
    /// <item><paramref name="setForcePasswordReset"/> = true/false sets the flag; null leaves it unchanged.</item>
    /// <item><paramref name="upgradeKeptSessionToFull"/> = true also clears the kept session's
    /// force-reset-limited state to full (the /account/password completion path).</item>
    /// </list>
    /// Returns the new credential epoch.
    /// </summary>
    Task<int> UpdateHashAndRevokeSessionsAsync(
        string userId,
        string passwordHash,
        int secretVersion,
        string? keepSessionId,
        bool? setForcePasswordReset,
        bool upgradeKeptSessionToFull,
        CancellationToken cancellationToken = default);
}
