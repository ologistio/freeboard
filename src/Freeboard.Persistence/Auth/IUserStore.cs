namespace Freeboard.Persistence.Auth;

/// <summary>The result of a guarded user disable.</summary>
public enum DisableUserOutcome
{
    Disabled,
    NotFound,
    LastSuperAdmin,
}

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

    /// <summary>
    /// Atomically creates an admin user (<c>global_role='admin'</c>). In ONE transaction: the user
    /// row, its force-reset flag, an optional password credential (null on the invite path, deferred
    /// until acceptance), AND the <c>super-admin</c> system assignment. If any write fails the whole
    /// create rolls back, leaving no orphan super-admin, so a counted super-admin can never exist
    /// without the credential it needs to authenticate. A duplicate normalized email surfaces as a
    /// unique-key violation (the caller maps it to a validation error).
    /// </summary>
    Task<UserRow> CreateAdminAsync(
        NewUser user,
        string? passwordHash,
        int secretVersion,
        bool forcePasswordReset,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Disables a user, rejecting the disable when the user is the LAST usable super-admin (enabled,
    /// holds <c>super-admin</c>, and has a credential). The guard runs atomically with the write
    /// (<c>SELECT ... FOR UPDATE</c> over the usable super-admin set inside the same transaction), so
    /// two concurrent disables cannot both leave the system with no usable administrator.
    /// </summary>
    Task<DisableUserOutcome> TryDisableUserAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Normalizes an email for the normalized column / lookups: trim + lower invariant.</summary>
    static string Normalize(string email)
    {
        ArgumentNullException.ThrowIfNull(email);
        return email.Trim().ToLowerInvariant();
    }
}
