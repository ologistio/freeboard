namespace Freeboard.Persistence.Auth;

/// <summary>
/// Recovery-code store. Codes are single-use, high-entropy, ALWAYS a valid MFA factor,
/// and stored only as a keyed HMAC (prefixless, with the key version recorded per row) so a
/// DB dump cannot recover them and they survive a key rotation. The plaintext set is returned
/// ONCE at generation; the store never exposes it again.
/// </summary>
public interface IRecoveryCodeStore
{
    /// <summary>
    /// Replaces the user's whole set with <paramref name="count"/> freshly generated codes,
    /// hashed under the current key id. Returns the plaintext codes once for display. A prior
    /// set (used or unused) is removed.
    /// </summary>
    Task<IReadOnlyList<string>> RegenerateAsync(string userId, int count, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically consumes a presented code: locates an unused row for the user whose stored
    /// HMAC (under its stored key version) matches, marks it used in a conditional update, and
    /// returns true only if THIS call performed the consume. A replay returns false.
    /// </summary>
    Task<bool> ConsumeAsync(string userId, string code, CancellationToken cancellationToken = default);

    /// <summary>Count of unused codes remaining for the user.</summary>
    Task<int> CountRemainingAsync(string userId, CancellationToken cancellationToken = default);
}
