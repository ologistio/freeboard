using System.Security.Cryptography;
using System.Text;
using Dapper;

namespace Freeboard.Persistence.Auth;

/// <summary>
/// MySQL-backed <see cref="IRecoveryCodeStore"/>. Each code is stored as a prefixless
/// keyed HMAC via <see cref="ITokenHasher"/> with the minting key version in
/// <c>token_key_version</c>, so verification under the STORED version survives a key rotation.
/// Consume is a conditional <c>UPDATE ... WHERE used_at IS NULL</c> so a code is consumable
/// exactly once.
/// </summary>
public sealed class MySqlRecoveryCodeStore(
    IDbConnectionFactory connectionFactory,
    IUlidFactory ulidFactory,
    ITokenHasher tokenHasher)
    : IRecoveryCodeStore
{
    // Crockford-style base32 alphabet (no I/L/O/U) for readable, transcription-safe codes.
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int GroupCount = 2;
    private const int GroupLength = 5; // 10 chars * 5 bits = 50 bits of entropy per code.

    public async Task<IReadOnlyList<string>> RegenerateAsync(string userId, int count, CancellationToken cancellationToken = default)
    {
        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "Recovery code count must be positive.");
        }

        var now = DateTime.UtcNow;
        var plaintext = new List<string>(count);
        var rows = new List<object>(count);
        for (var i = 0; i < count; i++)
        {
            var code = GenerateCode();
            var minted = tokenHasher.HashPrefixless(code);
            plaintext.Add(code);
            rows.Add(new
            {
                Id = ulidFactory.NewId(),
                UserId = userId,
                CodeHash = minted.Hash,
                TokenKeyVersion = minted.KeyVersion,
                Now = now,
            });
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM mfa_recovery_codes WHERE user_id = @UserId;",
            new { UserId = userId },
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO mfa_recovery_codes (id, user_id, code_hash, token_key_version, used_at, created_at) "
            + "VALUES (@Id, @UserId, @CodeHash, @TokenKeyVersion, NULL, @Now);",
            rows,
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return plaintext;
    }

    public async Task<bool> ConsumeAsync(string userId, string code, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(code);

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Codes are prefixless, so the matching row is found by verifying each unused row's
        // stored HMAC under its OWN stored key version (constant-time per row).
        var candidates = await connection.QueryAsync<(string Id, byte[] CodeHash, int TokenKeyVersion)>(
            new CommandDefinition(
                "SELECT id AS Id, code_hash AS CodeHash, token_key_version AS TokenKeyVersion "
                + "FROM mfa_recovery_codes WHERE user_id = @UserId AND used_at IS NULL;",
                new { UserId = userId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        string? matchedId = null;
        foreach (var (id, codeHash, keyVersion) in candidates)
        {
            if (tokenHasher.VerifyPrefixless(code, keyVersion, codeHash))
            {
                matchedId = id;
                break;
            }
        }

        if (matchedId is null)
        {
            return false;
        }

        // Conditional consume: only the call that flips used_at from NULL wins (single-use).
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE mfa_recovery_codes SET used_at = @Now WHERE id = @Id AND used_at IS NULL;",
            new { Id = matchedId, Now = DateTime.UtcNow },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<int> CountRemainingAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM mfa_recovery_codes WHERE user_id = @UserId AND used_at IS NULL;",
            new { UserId = userId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Generates one CSPRNG code formatted as hyphen-separated base32 groups (e.g. ABCDE-FGHJK).</summary>
    private static string GenerateCode()
    {
        var totalChars = GroupCount * GroupLength;
        var builder = new StringBuilder(totalChars + GroupCount - 1);
        for (var i = 0; i < totalChars; i++)
        {
            if (i > 0 && i % GroupLength == 0)
            {
                builder.Append('-');
            }

            builder.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
        }

        return builder.ToString();
    }
}
