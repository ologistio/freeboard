using System.Data;
using Dapper;

namespace Freeboard.Persistence.Auth;

/// <summary>
/// MySQL-backed <see cref="IMfaChallengeStore"/> using Dapper. The challenge token is
/// prefix-bearing (verified via <see cref="ITokenHasher.TryHashPrefixed"/>); the magic-link
/// token is prefixless and verified under the STORED <c>magic_link_token_key_version</c>.
/// Attempts, consume, and the send cap are all conditional single-statement updates
/// so concurrent verifies cannot lose a count or double-consume.
/// </summary>
public sealed class MySqlMfaChallengeStore(
    IDbConnectionFactory connectionFactory,
    IUlidFactory ulidFactory,
    ITokenHasher tokenHasher)
    : IMfaChallengeStore
{
    private const string SelectColumns =
        "id AS Id, user_id AS UserId, token_key_version AS TokenKeyVersion, credential_version AS CredentialVersion, "
        + "factors AS Factors, webauthn_options AS WebAuthnOptions, expires_at AS ExpiresAt, consumed_at AS ConsumedAt, "
        + "attempts AS Attempts, magic_link_sends AS MagicLinkSends, magic_link_expires_at AS MagicLinkExpiresAt, "
        + "created_at AS CreatedAt";

    public async Task<MintedMfaChallenge> CreateAsync(
        string userId,
        int credentialVersion,
        string factors,
        string? webAuthnOptions,
        DateTime expiresAt,
        CancellationToken cancellationToken = default)
    {
        var id = ulidFactory.NewId();
        var minted = tokenHasher.MintPrefixed();
        var now = DateTime.UtcNow;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO mfa_login_challenges "
            + "(id, challenge_token_hash, token_key_version, user_id, credential_version, factors, webauthn_options, "
            + "magic_link_token_hash, magic_link_token_key_version, magic_link_expires_at, magic_link_sends, "
            + "expires_at, consumed_at, attempts, created_at) "
            + "VALUES (@Id, @TokenHash, @TokenKeyVersion, @UserId, @CredentialVersion, @Factors, @WebAuthnOptions, "
            + "NULL, NULL, NULL, 0, @ExpiresAt, NULL, 0, @Now);",
            new
            {
                Id = id,
                TokenHash = minted.Hash,
                TokenKeyVersion = minted.KeyVersion,
                UserId = userId,
                CredentialVersion = credentialVersion,
                Factors = factors,
                WebAuthnOptions = webAuthnOptions,
                ExpiresAt = expiresAt,
                Now = now,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        var row = new MfaChallengeRow(
            id, userId, minted.KeyVersion, credentialVersion, factors, webAuthnOptions, expiresAt, null, 0, 0, null, now);
        return new MintedMfaChallenge(minted.Token, row);
    }

    public async Task<MfaChallengeRow?> FindByTokenAsync(string token, DateTime now, CancellationToken cancellationToken = default)
    {
        // Prefix-bearing: parse the key id and HMAC without a DB round-trip on malformed input.
        if (!tokenHasher.TryHashPrefixed(token, out var hash, out var keyVersion))
        {
            return null;
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<MfaChallengeRow>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM mfa_login_challenges "
            + "WHERE challenge_token_hash = @Hash AND consumed_at IS NULL AND expires_at > @Now;",
            new { Hash = hash, Now = now },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        // Integrity check: the parsed key id must equal the stored version.
        if (row is null || row.TokenKeyVersion != keyVersion)
        {
            return null;
        }

        return row;
    }

    // The sudo magic-link challenge is keyed by (user_id, sudo_dedupe_key) so at most one
    // active row exists per user. Login challenges leave sudo_dedupe_key NULL and never collide.
    private const string SudoMagicLinkDedupeKey = "magic_link";

    public async Task<SudoMagicLinkSendResult> FindOrCreateSudoMagicLinkAsync(
        string userId,
        int credentialVersion,
        byte[] magicLinkTokenHash,
        int magicLinkTokenKeyVersion,
        DateTime challengeExpiresAt,
        DateTime magicLinkExpiresAt,
        int maxSends,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(magicLinkTokenHash);

        var challengeToken = tokenHasher.MintPrefixed();

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

        // Find-or-create-or-reset the single sudo challenge row (the (user_id, sudo_dedupe_key) unique
        // key, migration 004). This manages only challenge identity/expiry/consume; the magic-link
        // tokens live in their own table. The upsert locks the row, so concurrent sudo sends for one
        // user serialise here and the active-token cap below cannot be multiplied by a race. A
        // consumed or expired row is reset in place (fresh identity, created_at bumped), which orphans
        // the prior instance's tokens because they keep the old created_at. consumed_at/expires_at are
        // assigned LAST so every IF predicate reads the ORIGINAL row state.
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO mfa_login_challenges "
            + "(id, challenge_token_hash, token_key_version, user_id, credential_version, factors, "
            + "webauthn_options, sudo_dedupe_key, magic_link_sends, expires_at, consumed_at, attempts, created_at) "
            + "VALUES (@Id, @ChallengeHash, @ChallengeKeyVersion, @UserId, @CredentialVersion, @Factors, "
            + "NULL, @DedupeKey, 0, @ExpiresAt, NULL, 0, @Now) "
            + "ON DUPLICATE KEY UPDATE "
            + "challenge_token_hash = IF(consumed_at IS NOT NULL OR expires_at <= @Now, @ChallengeHash, challenge_token_hash), "
            + "token_key_version = IF(consumed_at IS NOT NULL OR expires_at <= @Now, @ChallengeKeyVersion, token_key_version), "
            + "credential_version = IF(consumed_at IS NOT NULL OR expires_at <= @Now, @CredentialVersion, credential_version), "
            + "attempts = IF(consumed_at IS NOT NULL OR expires_at <= @Now, 0, attempts), "
            + "created_at = IF(consumed_at IS NOT NULL OR expires_at <= @Now, @Now, created_at), "
            // Assigned LAST so the predicate above reads the ORIGINAL consumed_at / expires_at.
            + "consumed_at = IF(consumed_at IS NOT NULL OR expires_at <= @Now, NULL, consumed_at), "
            + "expires_at = IF(consumed_at IS NOT NULL OR expires_at <= @Now, @ExpiresAt, expires_at);",
            new
            {
                Id = ulidFactory.NewId(),
                ChallengeHash = challengeToken.Hash,
                ChallengeKeyVersion = challengeToken.KeyVersion,
                UserId = userId,
                CredentialVersion = credentialVersion,
                Factors = SudoMagicLinkDedupeKey,
                DedupeKey = SudoMagicLinkDedupeKey,
                ExpiresAt = challengeExpiresAt,
                Now = now,
            },
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        // The single sudo row for this user. created_at identifies the current instance; tokens are
        // bound to it, so a prior instance's tokens (kept under the old created_at) never count here.
        var challenge = await connection.QuerySingleAsync<(string Id, DateTime CreatedAt)>(new CommandDefinition(
            "SELECT id AS Id, created_at AS CreatedAt FROM mfa_login_challenges "
            + "WHERE user_id = @UserId AND sudo_dedupe_key = @DedupeKey;",
            new { UserId = userId, DedupeKey = SudoMagicLinkDedupeKey },
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        // Bound table growth: drop tokens from prior instances and any that have expired. Current,
        // unexpired tokens stay; the active count below is the per-challenge re-send cap.
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM mfa_sudo_magic_link_tokens "
            + "WHERE challenge_id = @ChallengeId AND (challenge_created_at <> @CreatedAt OR expires_at <= @Now);",
            new { ChallengeId = challenge.Id, CreatedAt = challenge.CreatedAt, Now = now },
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        var activeTokens = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM mfa_sudo_magic_link_tokens "
            + "WHERE challenge_id = @ChallengeId AND challenge_created_at = @CreatedAt "
            + "AND consumed_at IS NULL AND expires_at > @Now;",
            new { ChallengeId = challenge.Id, CreatedAt = challenge.CreatedAt, Now = now },
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        var sent = false;
        if (activeTokens < maxSends)
        {
            // Each accepted send is its OWN token row, so a later send never clobbers this link.
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO mfa_sudo_magic_link_tokens "
                + "(id, challenge_id, challenge_created_at, token_hash, token_key_version, expires_at, consumed_at, created_at) "
                + "VALUES (@Id, @ChallengeId, @CreatedAt, @Hash, @KeyVersion, @LinkExpiresAt, NULL, @Now);",
                new
                {
                    Id = ulidFactory.NewId(),
                    ChallengeId = challenge.Id,
                    CreatedAt = challenge.CreatedAt,
                    Hash = magicLinkTokenHash,
                    KeyVersion = magicLinkTokenKeyVersion,
                    LinkExpiresAt = magicLinkExpiresAt,
                    Now = now,
                },
                transaction: transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            sent = true;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SudoMagicLinkSendResult(challenge.Id, sent);
    }

    public async Task<bool> RegisterFailedAttemptAsync(string id, int maxAttempts, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Bump attempts on an unconsumed row.
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE mfa_login_challenges SET attempts = attempts + 1 WHERE id = @Id AND consumed_at IS NULL;",
            new { Id = id },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        // Cap-reached consume: the affected-row count of THIS conditional UPDATE is the
        // cap-reached/consumed result, derived from the statement itself - no follow-up SELECT.
        // It matches (and consumes) exactly when the row is still unconsumed and the
        // just-incremented attempts have reached the cap; a replay finds the row already
        // consumed and affects zero rows.
        var consumedNow = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE mfa_login_challenges SET consumed_at = @Now "
            + "WHERE id = @Id AND consumed_at IS NULL AND attempts >= @MaxAttempts;",
            new { Id = id, MaxAttempts = maxAttempts, Now = now },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return consumedNow > 0;
    }

    public async Task<bool> ConsumeAsync(string id, DateTime now, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE mfa_login_challenges SET consumed_at = @Now WHERE id = @Id AND consumed_at IS NULL;",
            new { Id = id, Now = now },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<bool> SetMagicLinkAsync(
        string id,
        byte[] magicLinkTokenHash,
        int magicLinkTokenKeyVersion,
        DateTime magicLinkExpiresAt,
        int maxSends,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        // Conditional on the send cap and an unconsumed row; the increment and token set are atomic.
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE mfa_login_challenges "
            + "SET magic_link_token_hash = @Hash, magic_link_token_key_version = @KeyVersion, "
            + "magic_link_expires_at = @ExpiresAt, magic_link_sends = magic_link_sends + 1 "
            + "WHERE id = @Id AND consumed_at IS NULL AND magic_link_sends < @MaxSends;",
            new
            {
                Id = id,
                Hash = magicLinkTokenHash,
                KeyVersion = magicLinkTokenKeyVersion,
                ExpiresAt = magicLinkExpiresAt,
                MaxSends = maxSends,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<bool> VerifyMagicLinkAsync(string id, string magicLinkToken, DateTime now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(magicLinkToken);

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<(byte[]? Hash, int? KeyVersion, DateTime? ExpiresAt, DateTime? ConsumedAt)?>(
            new CommandDefinition(
                "SELECT magic_link_token_hash AS Hash, magic_link_token_key_version AS KeyVersion, "
                + "magic_link_expires_at AS ExpiresAt, consumed_at AS ConsumedAt "
                + "FROM mfa_login_challenges WHERE id = @Id;",
                new { Id = id },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (row is not { } r
            || r.Hash is null
            || r.KeyVersion is not { } keyVersion
            || r.ConsumedAt is not null
            || r.ExpiresAt is not { } expiresAt
            || expiresAt <= now)
        {
            return false;
        }

        // Prefixless: HMAC under the STORED key version, constant-time compare.
        return tokenHasher.VerifyPrefixless(magicLinkToken, keyVersion, r.Hash);
    }

    public async Task<bool> VerifyAndConsumeMagicLinkAsync(
        string id, string userId, string magicLinkToken, DateTime now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(magicLinkToken);

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        // The challenge must exist for THIS user (a challenge for user A cannot be used by B), be
        // unconsumed and unexpired. created_at scopes the tokens to the current instance.
        var challenge = await connection.QuerySingleOrDefaultAsync<(DateTime CreatedAt, DateTime ExpiresAt, DateTime? ConsumedAt)?>(
            new CommandDefinition(
                "SELECT created_at AS CreatedAt, expires_at AS ExpiresAt, consumed_at AS ConsumedAt "
                + "FROM mfa_login_challenges WHERE id = @Id AND user_id = @UserId;",
                new { Id = id, UserId = userId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (challenge is not { } c || c.ConsumedAt is not null || c.ExpiresAt <= now)
        {
            return false;
        }

        // Match the presented prefixless token against ANY active token of THIS instance, HMACing
        // under each token's stored key version (constant-time). At most maxSends candidates, so a
        // later send never invalidated an earlier emitted token.
        var candidates = await connection.QueryAsync<(string Id, byte[] Hash, int KeyVersion)>(new CommandDefinition(
            "SELECT id AS Id, token_hash AS Hash, token_key_version AS KeyVersion "
            + "FROM mfa_sudo_magic_link_tokens "
            + "WHERE challenge_id = @Id AND challenge_created_at = @CreatedAt "
            + "AND consumed_at IS NULL AND expires_at > @Now;",
            new { Id = id, CreatedAt = c.CreatedAt, Now = now },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        var matched = candidates.FirstOrDefault(t => tokenHasher.VerifyPrefixless(magicLinkToken, t.KeyVersion, t.Hash));
        if (matched.Hash is null)
        {
            return false;
        }

        // Atomic single-use consume of the CHALLENGE, bound to the user: only the call that flips
        // consumed_at from NULL wins, so the step-up is single-use even with multiple outstanding
        // tokens. A replay finds it consumed and affects zero rows.
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE mfa_login_challenges SET consumed_at = @Now WHERE id = @Id AND user_id = @UserId AND consumed_at IS NULL;",
            new { Id = id, UserId = userId, Now = now },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (affected == 0)
        {
            return false;
        }

        // Mark the matched token consumed for hygiene; the challenge consume already gates reuse.
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE mfa_sudo_magic_link_tokens SET consumed_at = @Now WHERE id = @TokenId AND consumed_at IS NULL;",
            new { TokenId = matched.Id, Now = now },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return true;
    }
}
