using Dapper;
using Freeboard.Persistence.Auth;

namespace Freeboard.Persistence;

/// <summary>
/// MySQL-backed <see cref="ICollectorCredentialStore"/>. Stores only the keyed-HMAC token hash and its
/// key version; the raw token never reaches the database. Revocation is scoped to the owning collector
/// and last-seen is a best-effort informational write. Mirrors <c>MySqlSessionStore</c>.
/// </summary>
public sealed class MySqlCollectorCredentialStore(IDbConnectionFactory connectionFactory, IUlidFactory ulidFactory)
    : ICollectorCredentialStore
{
    private const string SelectColumns =
        "id AS Id, collector_id AS CollectorId, token_key_version AS TokenKeyVersion, "
        + "created_at AS CreatedAt, last_seen_at AS LastSeenAt, expires_at AS ExpiresAt, revoked_at AS RevokedAt";

    public async Task<CollectorCredentialRow?> FindByTokenHashAsync(
        byte[] tokenHash, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<CollectorCredentialRow>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM collector_credentials WHERE token_hash = @TokenHash;",
            new { TokenHash = tokenHash },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<string> IssueAsync(
        string collectorId, byte[] tokenHash, int tokenKeyVersion, DateTime? expiresAt,
        CancellationToken cancellationToken = default)
    {
        var id = ulidFactory.NewId();
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO collector_credentials "
            + "(id, collector_id, token_hash, token_key_version, created_at, last_seen_at, expires_at, revoked_at) "
            + "VALUES (@Id, @CollectorId, @TokenHash, @TokenKeyVersion, @Now, NULL, @ExpiresAt, NULL);",
            new
            {
                Id = id,
                CollectorId = collectorId,
                TokenHash = tokenHash,
                TokenKeyVersion = tokenKeyVersion,
                Now = DateTime.UtcNow,
                ExpiresAt = expiresAt,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return id;
    }

    public async Task<bool> RevokeAsync(
        string collectorId, string credentialId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE collector_credentials SET revoked_at = @Now "
            + "WHERE id = @Id AND collector_id = @CollectorId AND revoked_at IS NULL;",
            new { Id = credentialId, CollectorId = collectorId, Now = DateTime.UtcNow },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<bool> TouchLastSeenAsync(
        string credentialId, DateTime seenAt, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE collector_credentials SET last_seen_at = @SeenAt WHERE id = @Id;",
            new { Id = credentialId, SeenAt = seenAt },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return affected > 0;
    }
}
