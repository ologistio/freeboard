using Dapper;
using OtpNet;

namespace Freeboard.Persistence.Auth;

/// <summary>
/// MySQL-backed <see cref="ITotpStore"/> using Otp.NET: SHA-1, 6 digits, 30s step,
/// +/-1 verification window. The secret is sealed with <see cref="ISecretProtector"/>
/// (AES-256-GCM) and stored as ciphertext/nonce/tag/key_version. Replay is blocked by a
/// conditional <c>UPDATE ... WHERE last_time_step IS NULL OR last_time_step &lt; @step</c>, so a
/// code accepted in one step cannot be re-used in the same step. Rotating an already-confirmed
/// secret stages the replacement in the pending_* columns and keeps the live secret working until
/// activation promotes the pending one.
/// </summary>
public sealed class MySqlTotpStore(IDbConnectionFactory connectionFactory, ISecretProtector secretProtector)
    : ITotpStore
{
    private const int SecretBytes = 20; // RFC 4226 recommended secret length.

    public async Task<TotpEnrollment> EnrollAsync(string userId, string accountName, string issuer, CancellationToken cancellationToken = default)
    {
        var secret = KeyGeneration.GenerateRandomKey(SecretBytes);
        var protectedSecret = secretProtector.Protect(secret);
        var base32Secret = Base32Encoding.ToString(secret);
        var provisioningUri = new OtpUri(OtpType.Totp, base32Secret, accountName, issuer).ToString();
        var now = DateTime.UtcNow;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        // No prior row, or an existing UNCONFIRMED secret: write/replace the live secret directly and
        // restart confirmation and the replay step. An existing CONFIRMED secret: keep it live and
        // working, and stage the new secret in the pending slot so an abandoned rotation never
        // destroys a usable factor. ActivateAsync promotes the pending secret to live.
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO totp_credentials "
            + "(user_id, secret_ciphertext, secret_nonce, secret_tag, key_version, confirmed_at, last_time_step, created_at) "
            + "VALUES (@UserId, @Ciphertext, @Nonce, @Tag, @KeyVersion, NULL, NULL, @Now) "
            + "ON DUPLICATE KEY UPDATE "
            + "secret_ciphertext = IF(confirmed_at IS NULL, @Ciphertext, secret_ciphertext), "
            + "secret_nonce = IF(confirmed_at IS NULL, @Nonce, secret_nonce), "
            + "secret_tag = IF(confirmed_at IS NULL, @Tag, secret_tag), "
            + "key_version = IF(confirmed_at IS NULL, @KeyVersion, key_version), "
            + "last_time_step = IF(confirmed_at IS NULL, NULL, last_time_step), "
            + "created_at = IF(confirmed_at IS NULL, @Now, created_at), "
            + "pending_secret_ciphertext = IF(confirmed_at IS NULL, NULL, @Ciphertext), "
            + "pending_secret_nonce = IF(confirmed_at IS NULL, NULL, @Nonce), "
            + "pending_secret_tag = IF(confirmed_at IS NULL, NULL, @Tag), "
            + "pending_key_version = IF(confirmed_at IS NULL, NULL, @KeyVersion), "
            + "pending_created_at = IF(confirmed_at IS NULL, NULL, @Now);",
            new
            {
                UserId = userId,
                Ciphertext = protectedSecret.Ciphertext,
                Nonce = protectedSecret.Nonce,
                Tag = protectedSecret.Tag,
                protectedSecret.KeyVersion,
                Now = now,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return new TotpEnrollment(provisioningUri);
    }

    public async Task<bool> ActivateAsync(string userId, string code, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(code);

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var row = await connection.QuerySingleOrDefaultAsync<ActivateRow>(new CommandDefinition(
            "SELECT secret_ciphertext AS SecretCiphertext, secret_nonce AS SecretNonce, secret_tag AS SecretTag, "
            + "key_version AS KeyVersion, pending_secret_ciphertext AS PendingCiphertext, "
            + "pending_secret_nonce AS PendingNonce, pending_secret_tag AS PendingTag, "
            + "pending_key_version AS PendingKeyVersion FROM totp_credentials WHERE user_id = @UserId;",
            new { UserId = userId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (row is null)
        {
            return false;
        }

        // A staged (pending) secret means this is a rotation of an already-confirmed factor: verify
        // the code against the NEW secret and, on success, promote it to live. With no pending secret
        // this is a first-time activation of the live secret.
        var rotating = row.PendingCiphertext is not null;
        var stored = rotating
            ? new ProtectedSecret(row.PendingCiphertext!, row.PendingNonce!, row.PendingTag!, row.PendingKeyVersion!.Value)
            : new ProtectedSecret(row.SecretCiphertext, row.SecretNonce, row.SecretTag, row.KeyVersion);

        var totp = new Totp(secretProtector.Unprotect(stored));
        if (!totp.VerifyTotp(code, out var matchedStep, new VerificationWindow(previous: 1, future: 1)))
        {
            return false;
        }

        var sql = rotating
            // Promote the staged secret to live and clear the pending slot, guarded on the slot still
            // being set so a concurrent activation cannot promote twice. last_time_step is reset to the
            // step just consumed so the activation code cannot be replayed against the new secret.
            ? "UPDATE totp_credentials SET secret_ciphertext = pending_secret_ciphertext, "
              + "secret_nonce = pending_secret_nonce, secret_tag = pending_secret_tag, "
              + "key_version = pending_key_version, confirmed_at = COALESCE(confirmed_at, @Now), "
              + "last_time_step = @Step, pending_secret_ciphertext = NULL, pending_secret_nonce = NULL, "
              + "pending_secret_tag = NULL, pending_key_version = NULL, pending_created_at = NULL "
              + "WHERE user_id = @UserId AND pending_secret_ciphertext IS NOT NULL;"
            // First-time activation: stamp confirmed_at and advance the replay step.
            : "UPDATE totp_credentials SET last_time_step = @Step, confirmed_at = COALESCE(confirmed_at, @Now) "
              + "WHERE user_id = @UserId AND (last_time_step IS NULL OR last_time_step < @Step);";

        var affected = await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { UserId = userId, Step = matchedStep, Now = DateTime.UtcNow },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<bool> VerifyAsync(string userId, string code, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(code);

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var row = await connection.QuerySingleOrDefaultAsync<(byte[] SecretCiphertext, byte[] SecretNonce, byte[] SecretTag, int KeyVersion, DateTime? ConfirmedAt)?>(
            new CommandDefinition(
                "SELECT secret_ciphertext AS SecretCiphertext, secret_nonce AS SecretNonce, secret_tag AS SecretTag, "
                + "key_version AS KeyVersion, confirmed_at AS ConfirmedAt FROM totp_credentials WHERE user_id = @UserId;",
                new { UserId = userId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        // Login uses only the CONFIRMED live secret; a pending (rotating) secret is never accepted.
        if (row is not { } r || r.ConfirmedAt is null)
        {
            return false;
        }

        var secret = secretProtector.Unprotect(
            new ProtectedSecret(r.SecretCiphertext, r.SecretNonce, r.SecretTag, r.KeyVersion));
        var totp = new Totp(secret);
        if (!totp.VerifyTotp(code, out var matchedStep, new VerificationWindow(previous: 1, future: 1)))
        {
            return false;
        }

        // Atomic replay guard: advance only if this step is strictly newer than the last used.
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE totp_credentials SET last_time_step = @Step "
            + "WHERE user_id = @UserId AND (last_time_step IS NULL OR last_time_step < @Step);",
            new { UserId = userId, Step = matchedStep },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<bool> IsConfirmedAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var confirmedAt = await connection.QuerySingleOrDefaultAsync<DateTime?>(new CommandDefinition(
            "SELECT confirmed_at FROM totp_credentials WHERE user_id = @UserId;",
            new { UserId = userId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return confirmedAt is not null;
    }

    public async Task<bool> DeleteAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM totp_credentials WHERE user_id = @UserId;",
            new { UserId = userId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return affected > 0;
    }

    // Activation read shape: the live secret plus the optional pending (rotating) secret.
    private sealed record ActivateRow(
        byte[] SecretCiphertext, byte[] SecretNonce, byte[] SecretTag, int KeyVersion,
        byte[]? PendingCiphertext, byte[]? PendingNonce, byte[]? PendingTag, int? PendingKeyVersion);
}
