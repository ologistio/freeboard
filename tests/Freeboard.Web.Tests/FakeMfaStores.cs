using System.Collections.Concurrent;
using Freeboard.Persistence.Auth;

namespace Freeboard.Web.Tests;

/// <summary>
/// In-memory <see cref="IMfaChallengeStore"/>. The challenge token is the lookup key directly
/// (the real impl HMACs it); attempts/consume/magic-link mirror the real atomic semantics.
/// </summary>
internal sealed class FakeMfaChallengeStore(ITokenHasher? tokenHasher = null) : IMfaChallengeStore
{
    private sealed class Entry
    {
        public required MfaChallengeRow Row { get; set; }

        // Login magic-link fallback: the single-slot keyed hash (SetMagicLinkAsync / VerifyMagicLinkAsync).
        public byte[]? MagicLinkHash { get; set; }

        public int MagicLinkKeyVersion { get; set; }

        // Sudo magic-link: one token per send, each bound to the challenge instance (Row.CreatedAt).
        public List<SudoToken> SudoTokens { get; } = [];
    }

    private sealed class SudoToken
    {
        public required byte[] Hash { get; init; }

        public required int KeyVersion { get; init; }

        public required DateTime ChallengeCreatedAt { get; init; }

        public required DateTime ExpiresAt { get; init; }

        public DateTime? ConsumedAt { get; set; }
    }

    private readonly ConcurrentDictionary<string, Entry> _byToken = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Entry> _byId = new(StringComparer.Ordinal);
    private int _seq;

    public Task<MintedMfaChallenge> CreateAsync(string userId, int credentialVersion, string factors, string? webAuthnOptions, DateTime expiresAt, CancellationToken cancellationToken = default)
    {
        var id = $"chal-{++_seq:D4}";
        var token = $"mfa-token-{id}";
        var row = new MfaChallengeRow(id, userId, 1, credentialVersion, factors, webAuthnOptions, expiresAt, null, 0, 0, null, DateTime.UtcNow);
        var entry = new Entry { Row = row };
        _byToken[token] = entry;
        _byId[id] = entry;
        return Task.FromResult(new MintedMfaChallenge(token, row));
    }

    public Task<MfaChallengeRow?> FindByTokenAsync(string token, DateTime now, CancellationToken cancellationToken = default)
    {
        if (_byToken.TryGetValue(token, out var entry)
            && entry.Row.ConsumedAt is null && entry.Row.ExpiresAt > now)
        {
            return Task.FromResult<MfaChallengeRow?>(entry.Row);
        }

        return Task.FromResult<MfaChallengeRow?>(null);
    }

    // Mirrors the real atomic find-or-create + record-one-send. A lock serializes the
    // find-or-create so concurrent first sends converge on ONE row, exactly like the SQL unique key.
    private readonly object _sudoLock = new();

    public Task<SudoMagicLinkSendResult> FindOrCreateSudoMagicLinkAsync(
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
        lock (_sudoLock)
        {
            // The single active sudo magic-link row for this user (factors == "magic_link").
            var entry = _byId.Values
                .Where(e => e.Row.UserId == userId && e.Row.Factors == "magic_link")
                .OrderByDescending(e => e.Row.CreatedAt)
                .FirstOrDefault();

            if (entry is null)
            {
                var id = $"chal-{++_seq:D4}";
                var token = $"mfa-token-{id}";
                var row = new MfaChallengeRow(
                    id, userId, 1, credentialVersion, "magic_link", null, challengeExpiresAt, null, 0, 0, null, now);
                entry = new Entry { Row = row };
                _byToken[token] = entry;
                _byId[id] = entry;
            }
            else if (entry.Row.ConsumedAt is not null || entry.Row.ExpiresAt <= now)
            {
                // Reset the expired/consumed instance in place; the new CreatedAt orphans its tokens.
                entry.Row = entry.Row with
                {
                    CredentialVersion = credentialVersion,
                    Attempts = 0,
                    ConsumedAt = null,
                    ExpiresAt = challengeExpiresAt,
                    CreatedAt = now,
                };
            }

            // Drop tokens from prior instances and any that have expired; the active count is the cap.
            entry.SudoTokens.RemoveAll(t => t.ChallengeCreatedAt != entry.Row.CreatedAt || t.ExpiresAt <= now);
            var active = entry.SudoTokens.Count(t => t.ConsumedAt is null && t.ExpiresAt > now);
            if (active >= maxSends)
            {
                return Task.FromResult(new SudoMagicLinkSendResult(entry.Row.Id, false));
            }

            entry.SudoTokens.Add(new SudoToken
            {
                Hash = magicLinkTokenHash,
                KeyVersion = magicLinkTokenKeyVersion,
                ChallengeCreatedAt = entry.Row.CreatedAt,
                ExpiresAt = magicLinkExpiresAt,
            });
            return Task.FromResult(new SudoMagicLinkSendResult(entry.Row.Id, true));
        }
    }

    public Task<bool> RegisterFailedAttemptAsync(string id, int maxAttempts, CancellationToken cancellationToken = default)
    {
        if (!_byId.TryGetValue(id, out var entry) || entry.Row.ConsumedAt is not null)
        {
            return Task.FromResult(false);
        }

        var attempts = entry.Row.Attempts + 1;
        var consumed = attempts >= maxAttempts ? (DateTime?)DateTime.UtcNow : null;
        entry.Row = entry.Row with { Attempts = attempts, ConsumedAt = consumed };
        return Task.FromResult(consumed is not null);
    }

    public Task<bool> ConsumeAsync(string id, DateTime now, CancellationToken cancellationToken = default)
    {
        if (!_byId.TryGetValue(id, out var entry) || entry.Row.ConsumedAt is not null)
        {
            return Task.FromResult(false);
        }

        entry.Row = entry.Row with { ConsumedAt = now };
        return Task.FromResult(true);
    }

    public Task<bool> SetMagicLinkAsync(string id, byte[] magicLinkTokenHash, int magicLinkTokenKeyVersion, DateTime magicLinkExpiresAt, int maxSends, CancellationToken cancellationToken = default)
    {
        if (!_byId.TryGetValue(id, out var entry) || entry.Row.ConsumedAt is not null || entry.Row.MagicLinkSends >= maxSends)
        {
            return Task.FromResult(false);
        }

        // Store the keyed hash + key version exactly like the real impl; verification re-HMACs the
        // presented plaintext under the stored key version (so this exercises the real hasher).
        entry.MagicLinkHash = magicLinkTokenHash;
        entry.MagicLinkKeyVersion = magicLinkTokenKeyVersion;
        entry.Row = entry.Row with { MagicLinkSends = entry.Row.MagicLinkSends + 1, MagicLinkExpiresAt = magicLinkExpiresAt };
        return Task.FromResult(true);
    }

    public Task<bool> VerifyMagicLinkAsync(string id, string magicLinkToken, DateTime now, CancellationToken cancellationToken = default)
    {
        if (!_byId.TryGetValue(id, out var entry)
            || entry.Row.ConsumedAt is not null
            || entry.MagicLinkHash is null
            || entry.Row.MagicLinkExpiresAt is not { } exp || exp <= now
            || tokenHasher is null)
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(
            tokenHasher.VerifyPrefixless(magicLinkToken, entry.MagicLinkKeyVersion, entry.MagicLinkHash));
    }

    public Task<bool> VerifyAndConsumeMagicLinkAsync(string id, string userId, string magicLinkToken, DateTime now, CancellationToken cancellationToken = default)
    {
        // Sudo path: bind to the user and the unconsumed, unexpired challenge, then match the token
        // against ANY active token of the current instance. A later send never clobbered an earlier
        // emitted token.
        if (!_byId.TryGetValue(id, out var entry)
            || entry.Row.UserId != userId
            || entry.Row.ConsumedAt is not null
            || entry.Row.ExpiresAt <= now
            || tokenHasher is null)
        {
            return Task.FromResult(false);
        }

        var matched = entry.SudoTokens.FirstOrDefault(t =>
            t.ChallengeCreatedAt == entry.Row.CreatedAt
            && t.ConsumedAt is null
            && t.ExpiresAt > now
            && tokenHasher.VerifyPrefixless(magicLinkToken, t.KeyVersion, t.Hash));
        if (matched is null)
        {
            return Task.FromResult(false);
        }

        // Single-use consume of the challenge gates reuse even with multiple outstanding tokens.
        entry.Row = entry.Row with { ConsumedAt = now };
        matched.ConsumedAt = now;
        return Task.FromResult(true);
    }

    /// <summary>Test helper: how many sudo magic-link challenge rows exist for a user.</summary>
    public int SudoMagicLinkChallengeCount(string userId)
        => _byId.Values.Count(e => e.Row.UserId == userId && e.Row.Factors == "magic_link");
}

/// <summary>In-memory <see cref="ITotpStore"/>. A user is "confirmed" once activated; verify accepts a configured code.</summary>
internal sealed class FakeTotpStore : ITotpStore
{
    private readonly ConcurrentDictionary<string, bool> _confirmed = new(StringComparer.Ordinal);

    /// <summary>The code that <see cref="VerifyAsync"/>/<see cref="ActivateAsync"/> accept.</summary>
    public string ValidCode { get; set; } = "123456";

    public Task<TotpEnrollment> EnrollAsync(string userId, string accountName, string issuer, CancellationToken cancellationToken = default)
        => Task.FromResult(new TotpEnrollment($"otpauth://totp/{issuer}:{accountName}?secret=ABC"));

    public Task<bool> ActivateAsync(string userId, string code, CancellationToken cancellationToken = default)
    {
        if (code != ValidCode)
        {
            return Task.FromResult(false);
        }

        _confirmed[userId] = true;
        return Task.FromResult(true);
    }

    public Task<bool> VerifyAsync(string userId, string code, CancellationToken cancellationToken = default)
        => Task.FromResult(_confirmed.GetValueOrDefault(userId) && code == ValidCode);

    public Task<bool> IsConfirmedAsync(string userId, CancellationToken cancellationToken = default)
        => Task.FromResult(_confirmed.GetValueOrDefault(userId));

    public Task<bool> DeleteAsync(string userId, CancellationToken cancellationToken = default)
        => Task.FromResult(_confirmed.TryRemove(userId, out _));

    /// <summary>Test helper: mark a user confirmed directly.</summary>
    public void SetConfirmed(string userId) => _confirmed[userId] = true;
}

/// <summary>In-memory <see cref="IRecoveryCodeStore"/>.</summary>
internal sealed class FakeRecoveryCodeStore : IRecoveryCodeStore
{
    private readonly ConcurrentDictionary<string, List<string>> _codes = new(StringComparer.Ordinal);

    public Task<IReadOnlyList<string>> RegenerateAsync(string userId, int count, CancellationToken cancellationToken = default)
    {
        var codes = Enumerable.Range(1, count).Select(i => $"{userId}-recovery-{i:D2}").ToList();
        _codes[userId] = [.. codes];
        return Task.FromResult<IReadOnlyList<string>>(codes);
    }

    public Task<bool> ConsumeAsync(string userId, string code, CancellationToken cancellationToken = default)
    {
        if (_codes.TryGetValue(userId, out var codes) && codes.Remove(code))
        {
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    public Task<int> CountRemainingAsync(string userId, CancellationToken cancellationToken = default)
        => Task.FromResult(_codes.TryGetValue(userId, out var codes) ? codes.Count : 0);

    /// <summary>Test helper: seed a recovery code set directly.</summary>
    public void Seed(string userId, params string[] codes) => _codes[userId] = [.. codes];
}

/// <summary>In-memory <see cref="IWebAuthnCredentialStore"/> (list/add/remove + counter rule).</summary>
internal sealed class FakeWebAuthnCredentialStore : IWebAuthnCredentialStore
{
    private readonly ConcurrentDictionary<string, WebAuthnCredentialRow> _byId = new(StringComparer.Ordinal);
    private int _seq;

    public Task<WebAuthnCredentialRow> AddAsync(NewWebAuthnCredential credential, CancellationToken cancellationToken = default)
    {
        var id = $"cred-{++_seq:D4}";
        var row = new WebAuthnCredentialRow(
            id, credential.UserId, credential.CredentialId, credential.PublicKey, credential.SignCount,
            credential.UserHandle, credential.Aaguid, credential.Transports, credential.CredType,
            credential.IsBackupEligible, credential.IsBackedUp, credential.Nickname, DateTime.UtcNow, null);
        _byId[id] = row;
        return Task.FromResult(row);
    }

    public Task<WebAuthnCredentialRow?> FindByCredentialIdAsync(byte[] credentialId, CancellationToken cancellationToken = default)
        => Task.FromResult(_byId.Values.FirstOrDefault(c => c.CredentialId.AsSpan().SequenceEqual(credentialId)));

    public Task<IReadOnlyList<WebAuthnCredentialRow>> ListByUserAsync(string userId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<WebAuthnCredentialRow>>(_byId.Values.Where(c => c.UserId == userId).ToList());

    public Task<bool> RemoveAsync(string id, CancellationToken cancellationToken = default)
        => Task.FromResult(_byId.TryRemove(id, out _));

    public Task<bool> UpdateSignCountAsync(string id, long presentedSignCount, DateTime usedAt, CancellationToken cancellationToken = default)
    {
        if (!_byId.TryGetValue(id, out var row) || !WebAuthnSignCounter.IsAcceptable(row.SignCount, presentedSignCount))
        {
            return Task.FromResult(false);
        }

        _byId[id] = row with { SignCount = presentedSignCount, LastUsedAt = usedAt };
        return Task.FromResult(true);
    }

    /// <summary>Test helper: seed a credential so the user "has a passkey".</summary>
    public void Seed(string userId, byte[] credentialId)
    {
        var id = $"cred-{++_seq:D4}";
        _byId[id] = new WebAuthnCredentialRow(
            id, userId, credentialId, [1, 2], 0, System.Text.Encoding.UTF8.GetBytes(userId),
            null, null, "public-key", null, null, "test", DateTime.UtcNow, null);
    }
}
