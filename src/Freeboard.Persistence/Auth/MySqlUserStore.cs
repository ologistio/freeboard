using System.Data;
using Dapper;
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

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new UserRow(id, displayEmail, normalized, admin.Name, admin.GlobalRole, true, false, false, now, now);
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
