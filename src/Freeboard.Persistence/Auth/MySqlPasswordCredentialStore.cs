using System.Data;
using Dapper;

namespace Freeboard.Persistence.Auth;

/// <summary>
/// MySQL-backed <see cref="IPasswordCredentialStore"/> using hand-written SQL via Dapper.
/// The hash lives only here, never on the user profile.
/// </summary>
public sealed class MySqlPasswordCredentialStore(IDbConnectionFactory connectionFactory) : IPasswordCredentialStore
{
    public async Task SetAsync(string userId, string passwordHash, int secretVersion, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO user_password_credentials (user_id, password_hash, secret_version, updated_at) "
            + "VALUES (@UserId, @PasswordHash, @SecretVersion, @Now) "
            + "ON DUPLICATE KEY UPDATE password_hash = @PasswordHash, secret_version = @SecretVersion, updated_at = @Now;",
            new { UserId = userId, PasswordHash = passwordHash, SecretVersion = secretVersion, Now = DateTime.UtcNow },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<PasswordCredentialRow?> GetAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<PasswordCredentialRow>(new CommandDefinition(
            "SELECT user_id AS UserId, password_hash AS PasswordHash, secret_version AS SecretVersion, "
            + "credential_version AS CredentialVersion "
            + "FROM user_password_credentials WHERE user_id = @UserId;",
            new { UserId = userId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<bool> UpdateHashAsync(
        string userId,
        string verifiedHash,
        int verifiedCredentialVersion,
        string newPasswordHash,
        int secretVersion,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        // Compare-and-swap. Only update when the row STILL holds the exact hash we verified and
        // the same credential epoch. If a password change/reset landed in the window, the WHERE clause
        // matches no row and we do NOT write the old-password-derived hash back. The credential epoch
        // is left unchanged (same password). 0 rows => leave the new password in place.
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE user_password_credentials SET password_hash = @NewPasswordHash, secret_version = @SecretVersion, "
            + "updated_at = @Now WHERE user_id = @UserId AND password_hash = @VerifiedHash "
            + "AND credential_version = @VerifiedCredentialVersion;",
            new
            {
                UserId = userId,
                VerifiedHash = verifiedHash,
                VerifiedCredentialVersion = verifiedCredentialVersion,
                NewPasswordHash = newPasswordHash,
                SecretVersion = secretVersion,
                Now = DateTime.UtcNow,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<int> UpdateHashAndRevokeSessionsAsync(
        string userId,
        string passwordHash,
        int secretVersion,
        string? keepSessionId,
        bool? setForcePasswordReset,
        bool upgradeKeptSessionToFull,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

        // 1. Set the new hash AND bump the credential epoch. Upsert in case the row is
        //    missing; the bump is COALESCE(credential_version, 0) + 1 on update.
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO user_password_credentials (user_id, password_hash, secret_version, credential_version, updated_at) "
            + "VALUES (@UserId, @PasswordHash, @SecretVersion, 1, @Now) "
            + "ON DUPLICATE KEY UPDATE password_hash = @PasswordHash, secret_version = @SecretVersion, "
            + "credential_version = credential_version + 1, updated_at = @Now;",
            new { UserId = userId, PasswordHash = passwordHash, SecretVersion = secretVersion, Now = now },
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        var newVersion = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT credential_version FROM user_password_credentials WHERE user_id = @UserId;",
            new { UserId = userId },
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        // 2. Optionally flip force_password_reset (admin reset sets it true; account/password false).
        if (setForcePasswordReset is { } force)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE users SET force_password_reset = @Force, updated_at = @Now WHERE id = @UserId;",
                new { UserId = userId, Force = force ? 1 : 0, Now = now },
                transaction: transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        // 3. Revoke sessions: keep the caller's current session when keepSessionId is provided
        //    (password change / account-password); otherwise revoke all (password/admin reset).
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM sessions WHERE user_id = @UserId AND (@Keep IS NULL OR id <> @Keep);",
            new { UserId = userId, Keep = keepSessionId },
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        // 4. Stamp the kept session's stored epoch to the new value so it is not self-invalidated;
        //    optionally upgrade it to full (the /account/password completion path).
        if (keepSessionId is not null)
        {
            var authStateSet = upgradeKeptSessionToFull ? ", auth_state = @Full" : string.Empty;
            await connection.ExecuteAsync(new CommandDefinition(
                $"UPDATE sessions SET credential_version = @NewVersion{authStateSet} WHERE id = @Keep;",
                new { Keep = keepSessionId, NewVersion = newVersion, Full = (int)SessionAuthState.Full },
                transaction: transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return newVersion;
    }
}
