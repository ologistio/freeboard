using Dapper;
using OtpNet;

namespace Freeboard.Persistence.Auth;

/// <summary>
/// MySQL-backed <see cref="ITotpStore"/> using Otp.NET: SHA-1, 6 digits, 30s step,
/// +/-1 verification window. The secret is sealed with <see cref="ISecretProtector"/>
/// (AES-256-GCM) and stored as ciphertext/nonce/tag/key_version. Replay is blocked by a
/// conditional <c>UPDATE ... WHERE last_time_step IS NULL OR last_time_step &lt; @step</c>, so a
/// code accepted in one step cannot be re-used in the same step.
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
        // Replace any prior secret: re-enrolling restarts confirmation and the replay step.
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO totp_credentials "
            + "(user_id, secret_ciphertext, secret_nonce, secret_tag, key_version, confirmed_at, last_time_step, created_at) "
            + "VALUES (@UserId, @Ciphertext, @Nonce, @Tag, @KeyVersion, NULL, NULL, @Now) "
            + "ON DUPLICATE KEY UPDATE secret_ciphertext = @Ciphertext, secret_nonce = @Nonce, secret_tag = @Tag, "
            + "key_version = @KeyVersion, confirmed_at = NULL, last_time_step = NULL, created_at = @Now;",
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

    public Task<bool> ActivateAsync(string userId, string code, CancellationToken cancellationToken = default)
        => VerifyAndAdvanceAsync(userId, code, requireConfirmed: false, confirmOnSuccess: true, cancellationToken);

    public Task<bool> VerifyAsync(string userId, string code, CancellationToken cancellationToken = default)
        => VerifyAndAdvanceAsync(userId, code, requireConfirmed: true, confirmOnSuccess: false, cancellationToken);

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

    private async Task<bool> VerifyAndAdvanceAsync(
        string userId, string code, bool requireConfirmed, bool confirmOnSuccess, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(code);

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var row = await connection.QuerySingleOrDefaultAsync<(byte[] SecretCiphertext, byte[] SecretNonce, byte[] SecretTag, int KeyVersion, DateTime? ConfirmedAt)?>(
            new CommandDefinition(
                "SELECT secret_ciphertext AS SecretCiphertext, secret_nonce AS SecretNonce, secret_tag AS SecretTag, "
                + "key_version AS KeyVersion, confirmed_at AS ConfirmedAt FROM totp_credentials WHERE user_id = @UserId;",
                new { UserId = userId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (row is not { } r || (requireConfirmed && r.ConfirmedAt is null))
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
        var sql = confirmOnSuccess
            ? "UPDATE totp_credentials SET last_time_step = @Step, confirmed_at = COALESCE(confirmed_at, @Now) "
              + "WHERE user_id = @UserId AND (last_time_step IS NULL OR last_time_step < @Step);"
            : "UPDATE totp_credentials SET last_time_step = @Step "
              + "WHERE user_id = @UserId AND (last_time_step IS NULL OR last_time_step < @Step);";

        var affected = await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { UserId = userId, Step = matchedStep, Now = DateTime.UtcNow },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return affected > 0;
    }
}
