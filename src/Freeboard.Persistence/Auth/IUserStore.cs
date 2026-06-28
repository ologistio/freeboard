namespace Freeboard.Persistence.Auth;

/// <summary>
/// User profile store. Hand-written SQL via Dapper. Profile reads NEVER carry the
/// password hash. Email lookups/uniqueness key on the normalized column (trim + lower,
/// invariant culture).
/// </summary>
public interface IUserStore
{
    /// <summary>
    /// Creates a user with a generated ULID id and the normalized email. Returns the
    /// created row. A duplicate normalized email surfaces as a unique-key violation from
    /// the database (the endpoint maps it to a validation error).
    /// </summary>
    Task<UserRow> CreateAsync(NewUser user, CancellationToken cancellationToken = default);

    Task<UserRow?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Looks up by raw email after normalizing it (trim + lower invariant).</summary>
    Task<UserRow?> GetByEmailAsync(string email, CancellationToken cancellationToken = default);

    Task SetEnabledAsync(string id, bool enabled, CancellationToken cancellationToken = default);

    Task SetForcePasswordResetAsync(string id, bool forcePasswordReset, CancellationToken cancellationToken = default);

    Task SetMfaEnabledAsync(string id, bool mfaEnabled, CancellationToken cancellationToken = default);

    /// <summary>Total user count. Used by the first-admin bootstrap path.</summary>
    Task<long> CountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all users (profiles only, no credentials), ordered by id (ULID time order).
    /// Used by the admin user-management list endpoint.
    /// </summary>
    Task<IReadOnlyList<UserRow>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// First-admin bootstrap. In ONE transaction, FIRST inserts the
    /// <c>bootstrap_marker</c> sentinel row (fixed PK 1); the PK collision means exactly one
    /// concurrent caller wins. Only the winner then inserts the admin user AND its password
    /// credential and commits. Returns the created admin row, or null when the marker already
    /// existed (a later/losing caller) so the endpoint maps that to 409. The hash is computed by
    /// the web layer and passed in so the SQL/transaction stays in this layer.
    /// </summary>
    Task<UserRow?> TryBootstrapAdminAsync(
        NewUser admin,
        string passwordHash,
        int secretVersion,
        CancellationToken cancellationToken = default);

    /// <summary>Normalizes an email for the normalized column / lookups: trim + lower invariant.</summary>
    static string Normalize(string email)
    {
        ArgumentNullException.ThrowIfNull(email);
        return email.Trim().ToLowerInvariant();
    }
}
