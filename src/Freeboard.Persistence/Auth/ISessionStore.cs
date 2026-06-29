namespace Freeboard.Persistence.Auth;

/// <summary>A persisted session row. Never carries the raw bearer token; only its keyed-HMAC hash.</summary>
public sealed record SessionRow(
    string Id,
    string UserId,
    int TokenKeyVersion,
    SessionAuthState AuthState,
    int CredentialVersion,
    DateTime? SudoAt,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    DateTime? LastSeenAt);

/// <summary>Session authentication state. Full access, or limited to the force-reset flow.</summary>
public enum SessionAuthState
{
    /// <summary>Fully authenticated.</summary>
    Full = 0,

    /// <summary>Force-password-reset-limited: only me/logout/account-password are allowed.</summary>
    ForceResetLimited = 1,
}

/// <summary>
/// Server-side session store. Storage-agnostic: no SQL/Dapper types in the
/// contract, so a Redis-backed store can replace the MySQL one. Sessions are opaque and
/// revocable; lookup is by the keyed-HMAC token hash.
/// </summary>
public interface ISessionStore
{
    /// <summary>
    /// Creates a session for a fully-formed bearer (the caller mints the token and its
    /// keyed-HMAC hash). Expiry is the caller-supplied absolute time.
    /// <paramref name="credentialVersion"/> is the user's current credential epoch at issue.
    /// </summary>
    Task<SessionRow> CreateAsync(
        string userId,
        byte[] tokenHash,
        int tokenKeyVersion,
        SessionAuthState authState,
        int credentialVersion,
        DateTime expiresAt,
        CancellationToken cancellationToken = default);

    /// <summary>Looks up a session by its keyed-HMAC token hash. Returns null if absent.</summary>
    Task<SessionRow?> FindByTokenHashAsync(byte[] tokenHash, CancellationToken cancellationToken = default);

    Task<SessionRow?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SessionRow>> ListByUserAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Deletes one session by id. Returns true if a row was removed.</summary>
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Deletes all sessions for a user ("log out everywhere", password change/reset). Returns the count removed.</summary>
    Task<int> DeleteAllForUserAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Stamps the step-up time (sudo-mode) on a session. Returns true if the row exists.</summary>
    Task<bool> SetSudoAtAsync(string id, DateTime sudoAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Upgrades a force-reset-limited session to full IN PLACE (same row, token unchanged) and
    /// stamps its stored credential epoch to <paramref name="credentialVersion"/>, so the
    /// session that just set the new password keeps working after the epoch bump. Returns true if
    /// a limited row was upgraded.
    /// </summary>
    Task<bool> UpgradeToFullAsync(string id, int credentialVersion, CancellationToken cancellationToken = default);

    /// <summary>Updates last-seen on a session. Returns true if the row exists.</summary>
    Task<bool> TouchLastSeenAsync(string id, DateTime seenAt, CancellationToken cancellationToken = default);

    /// <summary>Removes sessions whose expiry is at or before <paramref name="now"/>. Returns the count pruned.</summary>
    Task<int> PruneExpiredAsync(DateTime now, CancellationToken cancellationToken = default);
}
