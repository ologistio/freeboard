namespace Freeboard.Persistence.Auth;

/// <summary>A persisted MFA login challenge row. Never carries the raw challenge or magic-link token; only their hashes.</summary>
/// <param name="CredentialVersion">The credential epoch verified at the password step. Re-checked at completion.</param>
public sealed record MfaChallengeRow(
    string Id,
    string UserId,
    int TokenKeyVersion,
    int CredentialVersion,
    string Factors,
    string? WebAuthnOptions,
    DateTime ExpiresAt,
    DateTime? ConsumedAt,
    int Attempts,
    int MagicLinkSends,
    DateTime? MagicLinkExpiresAt,
    DateTime CreatedAt);

/// <summary>The minted challenge: the body-only token returned to the client once, plus the persisted row.</summary>
/// <param name="Token">The prefix-bearing <c>v&lt;keyId&gt;.&lt;secret&gt;</c> token. Body-only; never a session bearer.</param>
public readonly record struct MintedMfaChallenge(string Token, MfaChallengeRow Row);

/// <summary>
/// Result of an atomic sudo magic-link find-or-create: the id of the single active sudo
/// magic-link challenge for the user, and whether THIS send was accepted (false once the
/// per-challenge re-send cap is reached on the reused row).
/// </summary>
public readonly record struct SudoMagicLinkSendResult(string ChallengeId, bool Sent);

/// <summary>
/// MFA login-challenge store. Storage-agnostic: no SQL/Dapper types in the contract,
/// so a Redis (hashed keys + TTL) impl can replace the MySQL one. The challenge token is
/// prefix-bearing (keyed HMAC); the optional magic-link token is a SEPARATE prefixless secret
/// minted later with its OWN stored key version. Attempts and consume are atomic; the
/// row auto-consumes once a failure cap is reached.
/// </summary>
public interface IMfaChallengeStore
{
    /// <summary>
    /// Mints a prefix-bearing challenge token, persists the row (factors, optional webauthn
    /// options JSON, expiry, and the credential epoch verified at the password step), and
    /// returns the token once. The token is body-only.
    /// </summary>
    Task<MintedMfaChallenge> CreateAsync(
        string userId,
        int credentialVersion,
        string factors,
        string? webAuthnOptions,
        DateTime expiresAt,
        CancellationToken cancellationToken = default);

    /// <summary>Looks up an UNCONSUMED, UNEXPIRED challenge by its presented token. Returns null if absent/expired/consumed.</summary>
    Task<MfaChallengeRow?> FindByTokenAsync(string token, DateTime now, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically find-or-create the single active sudo magic-link challenge for <paramref name="userId"/>
    /// and record one magic-link send on it. Backed by a <c>(user_id, sudo_dedupe_key)</c>
    /// unique key and an <c>INSERT ... ON DUPLICATE KEY UPDATE</c>, so concurrent first sends converge
    /// on ONE row instead of each creating a fresh challenge that grants the full re-send budget.
    ///
    /// On the row: if none exists it is created with <paramref name="magicLinkTokenHash"/> and
    /// <c>magic_link_sends = 1</c>; if a consumed or expired sudo row exists it is RESET in place
    /// (fresh challenge token, sends back to 1, <c>consumed_at</c> cleared); if an active row exists
    /// and is under <paramref name="maxSends"/> the send is recorded (sends + 1, token replaced).
    /// Once the cap is reached the row is left unchanged and <see cref="SudoMagicLinkSendResult.Sent"/>
    /// is false. The caller mints the magic-link token and supplies its hash/key version.
    /// </summary>
    Task<SudoMagicLinkSendResult> FindOrCreateSudoMagicLinkAsync(
        string userId,
        int credentialVersion,
        byte[] magicLinkTokenHash,
        int magicLinkTokenKeyVersion,
        DateTime challengeExpiresAt,
        DateTime magicLinkExpiresAt,
        int maxSends,
        DateTime now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically records a failed attempt: increments <c>attempts</c> and, if the count reaches
    /// <paramref name="maxAttempts"/>, consumes the row in the same statement. Returns true if the
    /// row is now consumed (cap reached), false if attempts remain.
    /// </summary>
    Task<bool> RegisterFailedAttemptAsync(string id, int maxAttempts, CancellationToken cancellationToken = default);

    /// <summary>Atomically consumes the challenge (single-use). Returns true only if THIS call consumed it.</summary>
    Task<bool> ConsumeAsync(string id, DateTime now, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a magic-link token (prefixless keyed HMAC) plus its OWN key version and expiry on
    /// the challenge, and atomically increments <c>magic_link_sends</c>. Rejected (returns false,
    /// no write) once <paramref name="maxSends"/> is reached. The caller mints the token
    /// and computes its hash/key version via <see cref="ITokenHasher"/>.
    /// </summary>
    Task<bool> SetMagicLinkAsync(
        string id,
        byte[] magicLinkTokenHash,
        int magicLinkTokenKeyVersion,
        DateTime magicLinkExpiresAt,
        int maxSends,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies a presented magic-link token against the stored hash using the key named by the
    /// STORED <c>magic_link_token_key_version</c>, enforcing <c>magic_link_expires_at</c>.
    /// Does NOT consume the challenge; the caller consumes on success. Returns true on a match.
    /// </summary>
    Task<bool> VerifyMagicLinkAsync(string id, string magicLinkToken, DateTime now, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically verifies a magic-link token AND consumes the challenge in one step, bound
    /// to <paramref name="userId"/>: the row must match <c>id = @id AND user_id = @userId</c>,
    /// be unconsumed and unexpired, and the presented token must HMAC-match under the stored key
    /// version. Only the call that flips <c>consumed_at</c> returns true, so the token is single-use
    /// and a challenge for one user cannot be used against another. Used by both the sudo and login
    /// magic-link verifications.
    /// </summary>
    Task<bool> VerifyAndConsumeMagicLinkAsync(
        string id, string userId, string magicLinkToken, DateTime now, CancellationToken cancellationToken = default);
}
