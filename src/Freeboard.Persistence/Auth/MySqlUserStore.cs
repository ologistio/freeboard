using System.Data;
using Dapper;
using Freeboard.Core.Authz;
using MySqlConnector;

namespace Freeboard.Persistence.Auth;

/// <summary>
/// MySQL-backed <see cref="IUserStore"/> using hand-written SQL via Dapper. Profile reads
/// select only profile columns; the password hash lives in a separate table.
/// </summary>
public sealed class MySqlUserStore(IDbConnectionFactory connectionFactory, IUlidFactory ulidFactory) : IUserStore
{
    private const string SelectColumns =
        "id AS Id, email AS Email, email_normalized AS EmailNormalized, name AS Name, "
        + "global_role AS GlobalRole, enabled AS Enabled, force_password_reset AS ForcePasswordReset, "
        + "mfa_enabled AS MfaEnabled, created_at AS CreatedAt, updated_at AS UpdatedAt";

    public async Task<UserRow> CreateAsync(NewUser user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        var id = ulidFactory.NewId();
        // Store a trimmed display email (casing preserved); the normalized column is
        // trim + lower for uniqueness/lookup.
        var displayEmail = user.Email.Trim();
        var normalized = IUserStore.Normalize(user.Email);
        var now = DateTime.UtcNow;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO users "
            + "(id, email, email_normalized, name, global_role, enabled, force_password_reset, mfa_enabled, created_at, updated_at) "
            + "VALUES (@Id, @Email, @EmailNormalized, @Name, @GlobalRole, 1, 0, 0, @Now, @Now);",
            new
            {
                Id = id,
                Email = displayEmail,
                EmailNormalized = normalized,
                user.Name,
                user.GlobalRole,
                Now = now,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return new UserRow(id, displayEmail, normalized, user.Name, user.GlobalRole, true, false, false, now, now);
    }

    public async Task<UserRow?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM users WHERE id = @Id;",
            new { Id = id },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<UserRow?> GetByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        var normalized = IUserStore.Normalize(email);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM users WHERE email_normalized = @Normalized;",
            new { Normalized = normalized },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public Task SetEnabledAsync(string id, bool enabled, CancellationToken cancellationToken = default)
        => SetFlagAsync(id, "enabled", enabled, cancellationToken);

    public Task SetForcePasswordResetAsync(string id, bool forcePasswordReset, CancellationToken cancellationToken = default)
        => SetFlagAsync(id, "force_password_reset", forcePasswordReset, cancellationToken);

    public Task SetMfaEnabledAsync(string id, bool mfaEnabled, CancellationToken cancellationToken = default)
        => SetFlagAsync(id, "mfa_enabled", mfaEnabled, cancellationToken);

    public async Task<long> CountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM users;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<UserRow>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<UserRow>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM users ORDER BY id;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task<UserRow?> TryBootstrapAdminAsync(
        NewUser admin,
        string passwordHash,
        int secretVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(admin);
        var id = ulidFactory.NewId();
        var displayEmail = admin.Email.Trim();
        var normalized = IUserStore.Normalize(admin.Email);
        var now = DateTime.UtcNow;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

        try
        {
            // FIRST claim the sentinel: a duplicate-key collision means a first admin already
            // exists, so this caller loses the race and the whole transaction is abandoned.
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO bootstrap_marker (id, created_at) VALUES (1, @Now);",
                new { Now = now },
                transaction: transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO users "
            + "(id, email, email_normalized, name, global_role, enabled, force_password_reset, mfa_enabled, created_at, updated_at) "
            + "VALUES (@Id, @Email, @EmailNormalized, @Name, @GlobalRole, 1, 0, 0, @Now, @Now);",
            new { Id = id, Email = displayEmail, EmailNormalized = normalized, admin.Name, admin.GlobalRole, Now = now },
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO user_password_credentials (user_id, password_hash, secret_version, updated_at) "
            + "VALUES (@UserId, @PasswordHash, @SecretVersion, @Now);",
            new { UserId = id, PasswordHash = passwordHash, SecretVersion = secretVersion, Now = now },
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        // Fold the super-admin authz assignment into the same transaction as the user, credential, and
        // marker: the super-admin assignment (not the legacy global_role claim) is the sole system
        // power, so a fresh install must be administrable the moment bootstrap commits.
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO authz_system_role_assignments (user_id, role_key, created_at, updated_at) "
            + "VALUES (@UserId, @RoleKey, @Now, @Now);",
            new { UserId = id, RoleKey = AuthzRoles.SuperAdmin, Now = now },
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new UserRow(id, displayEmail, normalized, admin.Name, admin.GlobalRole, true, false, false, now, now);
    }

    public async Task<UserRow> CreateAdminAsync(
        NewUser user,
        string? passwordHash,
        int secretVersion,
        bool forcePasswordReset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        var id = ulidFactory.NewId();
        var displayEmail = user.Email.Trim();
        var normalized = IUserStore.Normalize(user.Email);
        var now = DateTime.UtcNow;
        var forceResetFlag = forcePasswordReset ? 1 : 0;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

        // The user row, its force-reset flag, an optional credential, AND the super-admin assignment
        // all commit together. A counted super-admin who cannot authenticate could otherwise let the
        // last usable admin be revoked/disabled; committing them as one unit closes that lockout hole.
        // The invite path passes a null credential (deferred to invite acceptance), so an invited admin
        // holds no credential yet and is not a usable super-admin until acceptance - by design.
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO users "
            + "(id, email, email_normalized, name, global_role, enabled, force_password_reset, mfa_enabled, created_at, updated_at) "
            + "VALUES (@Id, @Email, @EmailNormalized, @Name, @GlobalRole, 1, @ForceReset, 0, @Now, @Now);",
            new
            {
                Id = id,
                Email = displayEmail,
                EmailNormalized = normalized,
                user.Name,
                user.GlobalRole,
                ForceReset = forceResetFlag,
                Now = now,
            },
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (passwordHash is not null)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO user_password_credentials (user_id, password_hash, secret_version, updated_at) "
                + "VALUES (@UserId, @PasswordHash, @SecretVersion, @Now);",
                new { UserId = id, PasswordHash = passwordHash, SecretVersion = secretVersion, Now = now },
                transaction: transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO authz_system_role_assignments (user_id, role_key, created_at, updated_at) "
            + "VALUES (@UserId, @RoleKey, @Now, @Now);",
            new { UserId = id, RoleKey = AuthzRoles.SuperAdmin, Now = now },
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new UserRow(
            id, displayEmail, normalized, user.Name, user.GlobalRole, true, forcePasswordReset, false, now, now);
    }

    public async Task<DisableUserOutcome> TryDisableUserAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

        // Lock every usable super-admin's rows so a concurrent disable/revoke serialises behind this
        // one; the usable-survivor count is then race-free.
        // Credential presence is selected as the nullable credential user_id (maps cleanly to
        // string?); an IS NOT NULL expression returns a BIGINT that Dapper cannot bind to a bool.
        var superAdmins = (await connection.QueryAsync<(string UserId, bool Enabled, string? CredentialUserId)>(new CommandDefinition(
            "SELECT a.user_id AS UserId, u.enabled AS Enabled, c.user_id AS CredentialUserId "
            + "FROM authz_system_role_assignments a "
            + "JOIN users u ON u.id = a.user_id "
            + "LEFT JOIN user_password_credentials c ON c.user_id = a.user_id "
            + "WHERE a.role_key = @RoleKey FOR UPDATE;",
            new { RoleKey = AuthzRoles.SuperAdmin }, transaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false)).ToList();

        var exists = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM users WHERE id = @Id;",
            new { Id = id }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (exists == 0)
        {
            return DisableUserOutcome.NotFound;
        }

        var target = superAdmins.FirstOrDefault(r => string.Equals(r.UserId, id, StringComparison.Ordinal));
        var targetUsable = target is { Enabled: true, CredentialUserId: not null };
        var otherUsable = superAdmins.Any(r =>
            !string.Equals(r.UserId, id, StringComparison.Ordinal) && r is { Enabled: true, CredentialUserId: not null });
        if (targetUsable && !otherUsable)
        {
            return DisableUserOutcome.LastSuperAdmin;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE users SET enabled = 0, updated_at = @Now WHERE id = @Id;",
            new { Now = DateTime.UtcNow, Id = id }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return DisableUserOutcome.Disabled;
    }

    private async Task SetFlagAsync(string id, string column, bool value, CancellationToken cancellationToken)
    {
        // The column name is a fixed internal constant from the three Set* methods, never
        // caller input, so it is safe to interpolate. Values are parameterized.
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            $"UPDATE users SET {column} = @Value, updated_at = @Now WHERE id = @Id;",
            new { Value = value ? 1 : 0, Now = DateTime.UtcNow, Id = id },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
