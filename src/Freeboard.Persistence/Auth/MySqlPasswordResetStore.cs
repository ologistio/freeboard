using Dapper;

namespace Freeboard.Persistence.Auth;

/// <summary>
/// MySQL-backed <see cref="IPasswordResetStore"/>. The reset token is prefix-bearing:
/// minted via <see cref="ITokenHasher.MintPrefixed"/> and verified via
/// <see cref="ITokenHasher.TryHashPrefixed"/>, so a malformed/unknown-key token is rejected
/// without a DB lookup. Consume is a conditional <c>UPDATE ... WHERE used_at IS NULL</c> so a
/// token is usable exactly once.
/// </summary>
public sealed class MySqlPasswordResetStore(
    IDbConnectionFactory connectionFactory,
    IUlidFactory ulidFactory,
    ITokenHasher tokenHasher)
    : IPasswordResetStore
{
    public async Task<MintedPasswordReset> CreateAsync(string userId, DateTime expiresAt, CancellationToken cancellationToken = default)
    {
        var id = ulidFactory.NewId();
        var minted = tokenHasher.MintPrefixed();
        var now = DateTime.UtcNow;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO password_reset_tokens (id, user_id, token_hash, token_key_version, expires_at, used_at, created_at) "
            + "VALUES (@Id, @UserId, @TokenHash, @TokenKeyVersion, @ExpiresAt, NULL, @Now);",
            new
            {
                Id = id,
                UserId = userId,
                TokenHash = minted.Hash,
                TokenKeyVersion = minted.KeyVersion,
                ExpiresAt = expiresAt,
                Now = now,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return new MintedPasswordReset(minted.Token, id);
    }

    public async Task<string?> ConsumeAsync(string token, DateTime now, CancellationToken cancellationToken = default)
    {
        // Prefix-bearing: parse the key id and HMAC without a DB round-trip on malformed input.
        if (!tokenHasher.TryHashPrefixed(token, out var hash, out var keyVersion))
        {
            return null;
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<(string Id, string UserId, int TokenKeyVersion)?>(
            new CommandDefinition(
                "SELECT id AS Id, user_id AS UserId, token_key_version AS TokenKeyVersion "
                + "FROM password_reset_tokens "
                + "WHERE token_hash = @Hash AND used_at IS NULL AND expires_at > @Now;",
                new { Hash = hash, Now = now },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        // Integrity check: the parsed key id must equal the stored version.
        if (row is not { } r || r.TokenKeyVersion != keyVersion)
        {
            return null;
        }

        // Conditional consume: only the call that flips used_at from NULL wins (single-use).
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE password_reset_tokens SET used_at = @Now WHERE id = @Id AND used_at IS NULL;",
            new { Id = r.Id, Now = now },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return affected > 0 ? r.UserId : null;
    }
}
