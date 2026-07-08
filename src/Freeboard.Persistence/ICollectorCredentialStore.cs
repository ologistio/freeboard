namespace Freeboard.Persistence;

/// <summary>
/// A persisted per-collector machine credential. Never carries the raw token; only its keyed-HMAC hash
/// and the key version. <see cref="RevokedAt"/> and <see cref="ExpiresAt"/> are null when the credential
/// is live and unbounded; <see cref="LastSeenAt"/> is null until the credential first authenticates.
/// </summary>
public sealed record CollectorCredentialRow(
    string Id,
    string CollectorId,
    int TokenKeyVersion,
    DateTime CreatedAt,
    DateTime? LastSeenAt,
    DateTime? ExpiresAt,
    DateTime? RevokedAt);

/// <summary>
/// Store for per-collector machine credentials. Lookup is by the keyed-HMAC token hash; the raw token
/// is never persisted. Credentials are revocable and support an optional expiry. Existence of the
/// referenced collector is checked through the compliance read store, not here.
/// </summary>
public interface ICollectorCredentialStore
{
    /// <summary>Looks up a credential by its keyed-HMAC token hash. Returns null if absent.</summary>
    Task<CollectorCredentialRow?> FindByTokenHashAsync(byte[] tokenHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists a new credential for a collector (the caller mints the token and its keyed-HMAC hash).
    /// <paramref name="expiresAt"/> is an optional absolute expiry. Returns the new credential id.
    /// </summary>
    Task<string> IssueAsync(
        string collectorId,
        byte[] tokenHash,
        int tokenKeyVersion,
        DateTime? expiresAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes one credential, scoped to its owning collector. Returns true if a live row was revoked;
    /// false if it did not exist under that collector or was already revoked.
    /// </summary>
    Task<bool> RevokeAsync(string collectorId, string credentialId, CancellationToken cancellationToken = default);

    /// <summary>Best-effort last-seen update on successful authentication. Returns true if the row exists.</summary>
    Task<bool> TouchLastSeenAsync(string credentialId, DateTime seenAt, CancellationToken cancellationToken = default);
}
