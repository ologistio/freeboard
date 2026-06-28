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

        var id = ulidFactory.NewId();
        var challengeToken = tokenHasher.MintPrefixed();

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Atomic find-or-create + record-one-send in a single statement. The unique key on
        // (user_id, sudo_dedupe_key) makes concurrent first sends converge on ONE row.
        //
        // ON DUPLICATE KEY UPDATE branches on whether the existing row is RESETTABLE - consumed or
        // expired - inlined as (consumed_at IS NOT NULL OR expires_at <= @Now). MySQL evaluates the
        // assignments left to right and later ones see already-updated columns, so consumed_at and
        // expires_at (the two columns the reset predicate reads) are assigned LAST; every earlier
        // assignment therefore sees the ORIGINAL row state.
        //
        // Resettable -> rewrite as a brand-new challenge with magic_link_sends = 1. Active and under
        // the cap -> replace the magic-link token and increment sends. Active and AT the cap -> every
        // column keeps its current value (a no-op), rejecting the send; the follow-up read detects
        // this because the stored hash is not the one we just minted.
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO mfa_login_challenges "
            + "(id, challenge_token_hash, token_key_version, user_id, credential_version, factors, "
            + "webauthn_options, sudo_dedupe_key, magic_link_token_hash, magic_link_token_key_version, "
            + "magic_link_expires_at, magic_link_sends, expires_at, consumed_at, attempts, created_at) "
            + "VALUES (@Id, @ChallengeHash, @ChallengeKeyVersion, @UserId, @CredentialVersion, @Factors, "
            + "NULL, @DedupeKey, @LinkHash, @LinkKeyVersion, @LinkExpiresAt, 1, @ExpiresAt, NULL, 0, @Now) "
            + "ON DUPLICATE KEY UPDATE "
            // Challenge identity is rewritten only on reset; sends/token honour the cap when active.
            + "challenge_token_hash = IF(consumed_at IS NOT NULL OR expires_at <= @Now, @ChallengeHash, challenge_token_hash), "
            + "token_key_version = IF(consumed_at IS NOT NULL OR expires_at <= @Now, @ChallengeKeyVersion, token_key_version), "
            + "credential_version = IF(consumed_at IS NOT NULL OR expires_at <= @Now, @CredentialVersion, credential_version), "
            + "attempts = IF(consumed_at IS NOT NULL OR expires_at <= @Now, 0, attempts), "
            + "created_at = IF(consumed_at IS NOT NULL OR expires_at <= @Now, @Now, created_at), "
            + "magic_link_token_hash = IF(consumed_at IS NOT NULL OR expires_at <= @Now OR magic_link_sends < @MaxSends, @LinkHash, magic_link_token_hash), "
            + "magic_link_token_key_version = IF(consumed_at IS NOT NULL OR expires_at <= @Now OR magic_link_sends < @MaxSends, @LinkKeyVersion, magic_link_token_key_version), "
            + "magic_link_expires_at = IF(consumed_at IS NOT NULL OR expires_at <= @Now OR magic_link_sends < @MaxSends, @LinkExpiresAt, magic_link_expires_at), "
            + "magic_link_sends = IF(consumed_at IS NOT NULL OR expires_at <= @Now, 1, IF(magic_link_sends < @MaxSends, magic_link_sends + 1, magic_link_sends)), "
            // Assigned LAST so the predicate above reads the ORIGINAL consumed_at / expires_at.
            + "consumed_at = IF(consumed_at IS NOT NULL OR expires_at <= @Now, NULL, consumed_at), "
            + "expires_at = IF(consumed_at IS NOT NULL OR expires_at <= @Now, @ExpiresAt, expires_at);",
            new
            {
                Id = id,
                ChallengeHash = challengeToken.Hash,
                ChallengeKeyVersion = challengeToken.KeyVersion,
                UserId = userId,
                CredentialVersion = credentialVersion,
                Factors = SudoMagicLinkDedupeKey,
                DedupeKey = SudoMagicLinkDedupeKey,
                LinkHash = magicLinkTokenHash,
                LinkKeyVersion = magicLinkTokenKeyVersion,
                LinkExpiresAt = magicLinkExpiresAt,
                ExpiresAt = challengeExpiresAt,
                Now = now,
                MaxSends = maxSends,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        // Read back the resulting single row to report the id and whether our send landed.
        var row = await connection.QuerySingleAsync<(string Id, byte[]? Hash)>(new CommandDefinition(
            "SELECT id AS Id, magic_link_token_hash AS Hash FROM mfa_login_challenges "
            + "WHERE user_id = @UserId AND sudo_dedupe_key = @DedupeKey;",
            new { UserId = userId, DedupeKey = SudoMagicLinkDedupeKey },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        var sent = row.Hash is not null && row.Hash.AsSpan().SequenceEqual(magicLinkTokenHash);
        return new SudoMagicLinkSendResult(row.Id, sent);
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

        // Load the row bound to the CURRENT user: a challenge for user A cannot be used by B.
        var row = await connection.QuerySingleOrDefaultAsync<(byte[]? Hash, int? KeyVersion, DateTime? ExpiresAt, DateTime? ConsumedAt)?>(
            new CommandDefinition(
                "SELECT magic_link_token_hash AS Hash, magic_link_token_key_version AS KeyVersion, "
                + "magic_link_expires_at AS ExpiresAt, consumed_at AS ConsumedAt "
                + "FROM mfa_login_challenges WHERE id = @Id AND user_id = @UserId;",
                new { Id = id, UserId = userId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (row is not { } r
            || r.Hash is null
            || r.KeyVersion is not { } keyVersion
            || r.ConsumedAt is not null
            || r.ExpiresAt is not { } expiresAt
            || expiresAt <= now
            || !tokenHasher.VerifyPrefixless(magicLinkToken, keyVersion, r.Hash))
        {
            return false;
        }

        // Atomic single-use consume: only the call that flips consumed_at from NULL wins, and
        // it stays bound to the user. A replay finds the row consumed and affects zero rows.
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE mfa_login_challenges SET consumed_at = @Now WHERE id = @Id AND user_id = @UserId AND consumed_at IS NULL;",
            new { Id = id, UserId = userId, Now = now },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return affected > 0;
    }
}
