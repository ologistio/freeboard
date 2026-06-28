using Dapper;

namespace Freeboard.Persistence.Auth;

/// <summary>
/// MySQL-backed <see cref="ISessionStore"/> using hand-written SQL via Dapper. The
/// interface stays storage-agnostic; this is the default durable impl.
/// </summary>
public sealed class MySqlSessionStore(IDbConnectionFactory connectionFactory, IUlidFactory ulidFactory) : ISessionStore
{
    private const string SelectColumns =
        "id AS Id, user_id AS UserId, token_key_version AS TokenKeyVersion, auth_state AS AuthState, "
        + "credential_version AS CredentialVersion, sudo_at AS SudoAt, created_at AS CreatedAt, "
        + "expires_at AS ExpiresAt, last_seen_at AS LastSeenAt";

    // Materialization DTO. SessionRow.AuthState is the SessionAuthState enum; auth_state is TINYINT,
    // which MySqlConnector reports as SByte. Dapper's positional-record matcher requires the
    // constructor parameter type to match the column's reported CLR type exactly, and will not bind
    // an SByte column to an enum (or even a widened int) parameter - it throws "no matching
    // constructor". Read into this DTO whose AuthState is sbyte (matching SByte), then map to the
    // enum in code. Localized here so the storage-agnostic SessionRow contract is unchanged.
    private sealed record SessionRowDto(
        string Id,
        string UserId,
        int TokenKeyVersion,
        sbyte AuthState,
        int CredentialVersion,
        DateTime? SudoAt,
        DateTime CreatedAt,
        DateTime ExpiresAt,
        DateTime? LastSeenAt)
    {
        public SessionRow ToRow() => new(
            Id, UserId, TokenKeyVersion, (SessionAuthState)AuthState, CredentialVersion,
            SudoAt, CreatedAt, ExpiresAt, LastSeenAt);
    }

    public async Task<SessionRow> CreateAsync(
        string userId,
        byte[] tokenHash,
        int tokenKeyVersion,
        SessionAuthState authState,
        int credentialVersion,
        DateTime expiresAt,
        CancellationToken cancellationToken = default)
    {
        var id = ulidFactory.NewId();
        var now = DateTime.UtcNow;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO sessions "
            + "(id, user_id, token_hash, token_key_version, auth_state, credential_version, sudo_at, created_at, expires_at, last_seen_at) "
            + "VALUES (@Id, @UserId, @TokenHash, @TokenKeyVersion, @AuthState, @CredentialVersion, NULL, @Now, @ExpiresAt, NULL);",
            new
            {
                Id = id,
                UserId = userId,
                TokenHash = tokenHash,
                TokenKeyVersion = tokenKeyVersion,
                AuthState = (int)authState,
                CredentialVersion = credentialVersion,
                Now = now,
                ExpiresAt = expiresAt,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return new SessionRow(id, userId, tokenKeyVersion, authState, credentialVersion, null, now, expiresAt, null);
    }

    public async Task<SessionRow?> FindByTokenHashAsync(byte[] tokenHash, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var dto = await connection.QuerySingleOrDefaultAsync<SessionRowDto>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM sessions WHERE token_hash = @TokenHash;",
            new { TokenHash = tokenHash },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return dto?.ToRow();
    }

    public async Task<SessionRow?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var dto = await connection.QuerySingleOrDefaultAsync<SessionRowDto>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM sessions WHERE id = @Id;",
            new { Id = id },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return dto?.ToRow();
    }

    public async Task<IReadOnlyList<SessionRow>> ListByUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<SessionRowDto>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM sessions WHERE user_id = @UserId ORDER BY id;",
            new { UserId = userId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.Select(r => r.ToRow()).ToList();
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM sessions WHERE id = @Id;",
            new { Id = id },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<int> DeleteAllForUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM sessions WHERE user_id = @UserId;",
            new { UserId = userId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<bool> SetSudoAtAsync(string id, DateTime sudoAt, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE sessions SET sudo_at = @SudoAt WHERE id = @Id;",
            new { Id = id, SudoAt = sudoAt },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<bool> UpgradeToFullAsync(string id, int credentialVersion, CancellationToken cancellationToken = default)
    {
        // Upgrade in place; the token (and its hash) is unchanged. Only a limited row is upgraded.
        // Also stamp the new credential epoch so this just-upgraded session is not invalidated by
        // its own password change.
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE sessions SET auth_state = @Full, credential_version = @CredentialVersion "
            + "WHERE id = @Id AND auth_state = @Limited;",
            new
            {
                Id = id,
                Full = (int)SessionAuthState.Full,
                Limited = (int)SessionAuthState.ForceResetLimited,
                CredentialVersion = credentialVersion,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<bool> TouchLastSeenAsync(string id, DateTime seenAt, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE sessions SET last_seen_at = @SeenAt WHERE id = @Id;",
            new { Id = id, SeenAt = seenAt },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<int> PruneExpiredAsync(DateTime now, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM sessions WHERE expires_at <= @Now;",
            new { Now = now },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
