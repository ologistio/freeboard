namespace Freeboard.Persistence.Auth;

/// <summary>The minted reset: the prefix-bearing token returned to the caller once, plus its row id.</summary>
/// <param name="Token">The prefix-bearing <c>v&lt;keyId&gt;.&lt;secret&gt;</c> reset token. Emailed once; never stored.</param>
/// <param name="Id">The created row id (ULID).</param>
public readonly record struct MintedPasswordReset(string Token, string Id);

/// <summary>
/// Password-reset token store. The token is prefix-bearing (keyed HMAC at rest),
/// single-use, and expiry-bounded. Durable user data, so MySQL-only (not a Redis-swappable
/// hardening seam). Verify locates an unused, unexpired row by token hash and integrity-asserts
/// the stored key version equals the parsed key id.
/// </summary>
public interface IPasswordResetStore
{
    /// <summary>
    /// Mints a prefix-bearing reset token, persists the row for <paramref name="userId"/> with
    /// the given expiry, and returns the token once. The token is never re-retrievable.
    /// </summary>
    Task<MintedPasswordReset> CreateAsync(string userId, DateTime expiresAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically consumes a presented reset token: locates an unused, unexpired row whose keyed
    /// HMAC matches and whose stored key version equals the parsed key id, marks it used in a
    /// conditional update, and returns the owning user id only if THIS call performed the consume.
    /// Returns null for a malformed/unknown/expired/already-used token (no DB lookup on malformed
    /// input).
    /// </summary>
    Task<string?> ConsumeAsync(string token, DateTime now, CancellationToken cancellationToken = default);
}
