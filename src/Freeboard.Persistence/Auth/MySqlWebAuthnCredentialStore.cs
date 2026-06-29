using Dapper;

namespace Freeboard.Persistence.Auth;

/// <summary>
/// MySQL-backed <see cref="IWebAuthnCredentialStore"/> using Dapper. The sign-counter
/// update is a single conditional UPDATE that encodes the synced-passkey rule in its WHERE
/// clause, so the check-and-write is atomic; the pure <see cref="WebAuthnSignCounter"/>
/// stays for unit tests.
/// </summary>
public sealed class MySqlWebAuthnCredentialStore(IDbConnectionFactory connectionFactory, IUlidFactory ulidFactory)
    : IWebAuthnCredentialStore
{
    private const string SelectColumns =
        "id AS Id, user_id AS UserId, credential_id AS CredentialId, public_key AS PublicKey, "
        + "sign_count AS SignCount, user_handle AS UserHandle, aaguid AS Aaguid, transports AS Transports, "
        + "cred_type AS CredType, is_backup_eligible AS IsBackupEligible, is_backed_up AS IsBackedUp, "
        + "nickname AS Nickname, created_at AS CreatedAt, last_used_at AS LastUsedAt";

    // Materialization DTO. Dapper's positional-record matcher requires each constructor parameter
    // type to match the column's reported CLR type exactly. MySqlConnector reports sign_count
    // BIGINT UNSIGNED as UInt64, aaguid CHAR(36) as Guid, and is_backup_eligible/is_backed_up
    // TINYINT(1) as Boolean - none of which bind to the storage-agnostic WebAuthnCredentialRow's
    // long / string? / bool? members. Read into this DTO whose types match what the driver
    // reports, then map. Keeps the public contract unchanged.
    private sealed record WebAuthnCredentialRowDto(
        string Id,
        string UserId,
        byte[] CredentialId,
        byte[] PublicKey,
        ulong SignCount,
        byte[] UserHandle,
        Guid? Aaguid,
        string? Transports,
        string? CredType,
        bool? IsBackupEligible,
        bool? IsBackedUp,
        string? Nickname,
        DateTime CreatedAt,
        DateTime? LastUsedAt)
    {
        public WebAuthnCredentialRow ToRow() => new(
            Id, UserId, CredentialId, PublicKey, (long)SignCount, UserHandle,
            Aaguid?.ToString(), Transports, CredType, IsBackupEligible, IsBackedUp,
            Nickname, CreatedAt, LastUsedAt);
    }

    public async Task<WebAuthnCredentialRow> AddAsync(NewWebAuthnCredential credential, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var id = ulidFactory.NewId();
        var now = DateTime.UtcNow;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO webauthn_credentials "
            + "(id, user_id, credential_id, public_key, sign_count, user_handle, aaguid, transports, cred_type, "
            + "is_backup_eligible, is_backed_up, nickname, created_at, last_used_at) "
            + "VALUES (@Id, @UserId, @CredentialId, @PublicKey, @SignCount, @UserHandle, @Aaguid, @Transports, "
            + "@CredType, @IsBackupEligible, @IsBackedUp, @Nickname, @Now, NULL);",
            new
            {
                Id = id,
                credential.UserId,
                credential.CredentialId,
                credential.PublicKey,
                credential.SignCount,
                credential.UserHandle,
                credential.Aaguid,
                credential.Transports,
                credential.CredType,
                credential.IsBackupEligible,
                credential.IsBackedUp,
                credential.Nickname,
                Now = now,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return new WebAuthnCredentialRow(
            id, credential.UserId, credential.CredentialId, credential.PublicKey, credential.SignCount,
            credential.UserHandle, credential.Aaguid, credential.Transports, credential.CredType,
            credential.IsBackupEligible, credential.IsBackedUp, credential.Nickname, now, null);
    }

    public async Task<WebAuthnCredentialRow?> FindByCredentialIdAsync(byte[] credentialId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var dto = await connection.QuerySingleOrDefaultAsync<WebAuthnCredentialRowDto>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM webauthn_credentials WHERE credential_id = @CredentialId;",
            new { CredentialId = credentialId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return dto?.ToRow();
    }

    public async Task<IReadOnlyList<WebAuthnCredentialRow>> ListByUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<WebAuthnCredentialRowDto>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM webauthn_credentials WHERE user_id = @UserId ORDER BY id;",
            new { UserId = userId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.Select(r => r.ToRow()).ToList();
    }

    public async Task<bool> RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM webauthn_credentials WHERE id = @Id;",
            new { Id = id },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<bool> UpdateSignCountAsync(string id, long presentedSignCount, DateTime usedAt, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        // The synced-passkey rule is enforced atomically in SQL, not via a separate
        // read-then-write. The WHERE clause encodes WebAuthnSignCounter.IsAcceptable: accept a
        // presented 0 (synced passkey) or a stored 0, else require a strict increase. No row
        // matches (and we return false) when the presented counter is a positive regression OR
        // the id is unknown. The pure WebAuthnSignCounter stays for unit tests.
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE webauthn_credentials SET sign_count = @Presented, last_used_at = @UsedAt "
            + "WHERE id = @Id AND (@Presented = 0 OR sign_count = 0 OR @Presented > sign_count);",
            new { Id = id, Presented = presentedSignCount, UsedAt = usedAt },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return affected > 0;
    }
}
