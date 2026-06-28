using System.Collections.Concurrent;
using Freeboard.Persistence.Auth;

namespace Freeboard.Web.Tests;

/// <summary>
/// In-memory <see cref="ISessionStore"/> for web auth tests. Full behavior so login/logout,
/// session management, and revocation can be asserted without MySQL.
/// </summary>
internal sealed class FakeSessionStore : ISessionStore
{
    private readonly Dictionary<string, SessionRow> _byId = new(StringComparer.Ordinal);
    private readonly List<(byte[] Hash, string Id)> _byHash = [];
    private int _seq;

    /// <summary>Seeds a session with a known token hash (bearer-handler tests).</summary>
    public SessionRow Add(SessionRow row, byte[] tokenHash)
    {
        _byId[row.Id] = row;
        _byHash.Add((tokenHash, row.Id));
        return row;
    }

    public IReadOnlyList<SessionRow> All => _byId.Values.ToList();

    public Task<SessionRow> CreateAsync(string userId, byte[] tokenHash, int tokenKeyVersion, SessionAuthState authState, int credentialVersion, DateTime expiresAt, CancellationToken cancellationToken = default)
    {
        var id = $"sess-{++_seq:D4}";
        var row = new SessionRow(id, userId, tokenKeyVersion, authState, credentialVersion, null, DateTime.UtcNow, expiresAt, null);
        _byId[id] = row;
        _byHash.Add((tokenHash, id));
        return Task.FromResult(row);
    }

    /// <summary>Test convenience: create a session at credential epoch 1.</summary>
    public Task<SessionRow> CreateAsync(string userId, byte[] tokenHash, int tokenKeyVersion, SessionAuthState authState, DateTime expiresAt, CancellationToken cancellationToken = default)
        => CreateAsync(userId, tokenHash, tokenKeyVersion, authState, 1, expiresAt, cancellationToken);

    public Task<SessionRow?> FindByTokenHashAsync(byte[] tokenHash, CancellationToken cancellationToken = default)
    {
        foreach (var (hash, id) in _byHash)
        {
            if (hash.AsSpan().SequenceEqual(tokenHash) && _byId.ContainsKey(id))
            {
                return Task.FromResult<SessionRow?>(_byId[id]);
            }
        }

        return Task.FromResult<SessionRow?>(null);
    }

    public Task<SessionRow?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
        => Task.FromResult(_byId.GetValueOrDefault(id));

    public Task<IReadOnlyList<SessionRow>> ListByUserAsync(string userId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<SessionRow>>(
            _byId.Values.Where(s => s.UserId == userId).OrderBy(s => s.Id, StringComparer.Ordinal).ToList());

    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
        => Task.FromResult(_byId.Remove(id));

    public Task<int> DeleteAllForUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        var ids = _byId.Values.Where(s => s.UserId == userId).Select(s => s.Id).ToList();
        foreach (var id in ids)
        {
            _byId.Remove(id);
        }

        return Task.FromResult(ids.Count);
    }

    public Task<bool> SetSudoAtAsync(string id, DateTime sudoAt, CancellationToken cancellationToken = default)
    {
        if (!_byId.TryGetValue(id, out var row))
        {
            return Task.FromResult(false);
        }

        _byId[id] = row with { SudoAt = sudoAt };
        return Task.FromResult(true);
    }

    public Task<bool> UpgradeToFullAsync(string id, int credentialVersion, CancellationToken cancellationToken = default)
    {
        if (!_byId.TryGetValue(id, out var row) || row.AuthState != SessionAuthState.ForceResetLimited)
        {
            return Task.FromResult(false);
        }

        _byId[id] = row with { AuthState = SessionAuthState.Full, CredentialVersion = credentialVersion };
        return Task.FromResult(true);
    }

    /// <summary>Test helper: directly set a session's stored credential epoch.</summary>
    public void SetCredentialVersion(string id, int credentialVersion)
    {
        if (_byId.TryGetValue(id, out var row))
        {
            _byId[id] = row with { CredentialVersion = credentialVersion };
        }
    }

    public Task<bool> TouchLastSeenAsync(string id, DateTime seenAt, CancellationToken cancellationToken = default)
    {
        if (!_byId.TryGetValue(id, out var row))
        {
            return Task.FromResult(false);
        }

        _byId[id] = row with { LastSeenAt = seenAt };
        return Task.FromResult(true);
    }

    public Task<int> PruneExpiredAsync(DateTime now, CancellationToken cancellationToken = default)
        => Task.FromResult(0);
}

/// <summary>In-memory <see cref="IUserStore"/> for web auth tests.</summary>
internal sealed class FakeUserStore : IUserStore
{
    private readonly Dictionary<string, UserRow> _byId = new(StringComparer.Ordinal);
    private int _seq;
    private bool _bootstrapped;

    public UserRow Add(UserRow user)
    {
        _byId[user.Id] = user;
        return user;
    }

    public Task<UserRow> CreateAsync(NewUser user, CancellationToken cancellationToken = default)
    {
        var normalized = IUserStore.Normalize(user.Email);
        var id = $"user-{++_seq:D4}";
        var now = DateTime.UtcNow;
        var row = new UserRow(id, user.Email.Trim(), normalized, user.Name, user.GlobalRole, true, false, false, now, now);
        _byId[id] = row;
        return Task.FromResult(row);
    }

    public Task<UserRow?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
        => Task.FromResult(_byId.GetValueOrDefault(id));

    public Task<UserRow?> GetByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        var normalized = IUserStore.Normalize(email);
        return Task.FromResult(_byId.Values.FirstOrDefault(u => u.EmailNormalized == normalized));
    }

    public Task SetEnabledAsync(string id, bool enabled, CancellationToken cancellationToken = default)
    {
        if (_byId.TryGetValue(id, out var row))
        {
            _byId[id] = row with { Enabled = enabled };
        }

        return Task.CompletedTask;
    }

    public Task SetForcePasswordResetAsync(string id, bool forcePasswordReset, CancellationToken cancellationToken = default)
    {
        if (_byId.TryGetValue(id, out var row))
        {
            _byId[id] = row with { ForcePasswordReset = forcePasswordReset };
        }

        return Task.CompletedTask;
    }

    public Task SetMfaEnabledAsync(string id, bool mfaEnabled, CancellationToken cancellationToken = default)
    {
        if (_byId.TryGetValue(id, out var row))
        {
            _byId[id] = row with { MfaEnabled = mfaEnabled };
        }

        return Task.CompletedTask;
    }

    public Task<long> CountAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<long>(_byId.Count);

    public Task<IReadOnlyList<UserRow>> ListAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<UserRow>>(
            _byId.Values.OrderBy(u => u.Id, StringComparer.Ordinal).ToList());

    public Task<UserRow?> TryBootstrapAdminAsync(NewUser admin, string passwordHash, int secretVersion, CancellationToken cancellationToken = default)
    {
        // Mirror the sentinel-PK race guard: the first call wins, later calls collide -> null.
        if (_bootstrapped)
        {
            return Task.FromResult<UserRow?>(null);
        }

        _bootstrapped = true;
        var id = $"user-{++_seq:D4}";
        var now = DateTime.UtcNow;
        var normalized = IUserStore.Normalize(admin.Email);
        var row = new UserRow(id, admin.Email.Trim(), normalized, admin.Name, admin.GlobalRole, true, false, false, now, now);
        _byId[id] = row;
        return Task.FromResult<UserRow?>(row);
    }
}

/// <summary>
/// In-memory <see cref="IPasswordCredentialStore"/>. Optionally wired to the session and user
/// fakes so <see cref="UpdateHashAndRevokeSessionsAsync"/> performs the same combined effect the
/// real transactional method does (set hash, optional force-reset flip, revoke sessions) - so the
/// atomicity tests can assert revocation.
/// </summary>
internal sealed class FakeCredentialStore(FakeSessionStore? sessions = null, FakeUserStore? users = null)
    : IPasswordCredentialStore
{
    private readonly ConcurrentDictionary<string, PasswordCredentialRow> _byUser = new(StringComparer.Ordinal);

    /// <summary>When true, UpdateHashAsync throws - to prove the login rehash is non-fatal.</summary>
    public bool ThrowOnUpdateHash { get; set; }

    /// <summary>
    /// When true, every GetAsync returns the CURRENT row then bumps the epoch: models a
    /// concurrent password change landing right after a read. With the fix, login reads once (the
    /// verify) and stamps the session with that read's epoch; a later read sees the bumped epoch and
    /// the bearer check rejects the session. (The old code re-read the epoch at issue and would have
    /// stamped the bumped value, wrongly accepting it.)
    /// </summary>
    public bool BumpOnGet { get; set; }

    public Task SetAsync(string userId, string passwordHash, int secretVersion, CancellationToken cancellationToken = default)
    {
        // Preserve the current epoch on overwrite; a fresh row starts at 1.
        var version = _byUser.TryGetValue(userId, out var existing) ? existing.CredentialVersion : 1;
        _byUser[userId] = new PasswordCredentialRow(userId, passwordHash, secretVersion, version);
        return Task.CompletedTask;
    }

    public Task<PasswordCredentialRow?> GetAsync(string userId, CancellationToken cancellationToken = default)
    {
        var row = _byUser.GetValueOrDefault(userId);
        if (BumpOnGet && row is not null)
        {
            _byUser[userId] = row with { CredentialVersion = row.CredentialVersion + 1 };
        }

        return Task.FromResult(row);
    }

    /// <summary>
    /// Test helper: bump ONLY the credential epoch (leaving any sessions' stored epoch untouched),
    /// modelling a password change whose session-revocation raced with a concurrent login.
    /// </summary>
    public void BumpCredentialVersionOnly(string userId)
    {
        if (_byUser.TryGetValue(userId, out var row))
        {
            _byUser[userId] = row with { CredentialVersion = row.CredentialVersion + 1 };
        }
    }

    public Task<bool> UpdateHashAsync(
        string userId,
        string verifiedHash,
        int verifiedCredentialVersion,
        string newPasswordHash,
        int secretVersion,
        CancellationToken cancellationToken = default)
    {
        if (ThrowOnUpdateHash)
        {
            throw new InvalidOperationException("simulated DB failure during rehash");
        }

        // Compare-and-swap. Only update when the stored row STILL holds the exact hash we
        // verified AND the same credential epoch; a row that changed underneath is left alone so a
        // newer password is never clobbered. The epoch is NOT bumped (same password).
        if (!_byUser.TryGetValue(userId, out var existing)
            || existing.PasswordHash != verifiedHash
            || existing.CredentialVersion != verifiedCredentialVersion)
        {
            return Task.FromResult(false);
        }

        _byUser[userId] = existing with { PasswordHash = newPasswordHash, SecretVersion = secretVersion };
        return Task.FromResult(true);
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
        // Bump the credential epoch.
        var newVersion = (_byUser.TryGetValue(userId, out var existing) ? existing.CredentialVersion : 0) + 1;
        _byUser[userId] = new PasswordCredentialRow(userId, passwordHash, secretVersion, newVersion);

        if (setForcePasswordReset is { } force && users is not null)
        {
            await users.SetForcePasswordResetAsync(userId, force, cancellationToken).ConfigureAwait(false);
        }

        if (sessions is not null)
        {
            var rows = await sessions.ListByUserAsync(userId, cancellationToken).ConfigureAwait(false);
            foreach (var row in rows)
            {
                if (keepSessionId is null || !string.Equals(row.Id, keepSessionId, StringComparison.Ordinal))
                {
                    await sessions.DeleteAsync(row.Id, cancellationToken).ConfigureAwait(false);
                }
            }

            // Stamp the kept session's epoch to the new value; optionally upgrade it to full.
            if (keepSessionId is not null)
            {
                if (upgradeKeptSessionToFull)
                {
                    await sessions.UpgradeToFullAsync(keepSessionId, newVersion, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    sessions.SetCredentialVersion(keepSessionId, newVersion);
                }
            }
        }

        return newVersion;
    }
}

/// <summary>In-memory <see cref="IPasswordResetStore"/>; single-use, expiry-bounded.</summary>
internal sealed class FakeResetStore : IPasswordResetStore
{
    private readonly Dictionary<string, (string UserId, DateTime ExpiresAt, bool Used)> _byToken = new(StringComparer.Ordinal);
    private int _seq;

    public Task<MintedPasswordReset> CreateAsync(string userId, DateTime expiresAt, CancellationToken cancellationToken = default)
    {
        var token = $"reset-token-{++_seq}";
        var id = $"reset-{_seq}";
        _byToken[token] = (userId, expiresAt, false);
        return Task.FromResult(new MintedPasswordReset(token, id));
    }

    public Task<string?> ConsumeAsync(string token, DateTime now, CancellationToken cancellationToken = default)
    {
        if (!_byToken.TryGetValue(token, out var row) || row.Used || row.ExpiresAt <= now)
        {
            return Task.FromResult<string?>(null);
        }

        _byToken[token] = (row.UserId, row.ExpiresAt, true);
        return Task.FromResult<string?>(row.UserId);
    }
}

/// <summary>
/// Recording <see cref="IPasswordHasher"/>: tracks whether Verify and VerifyDecoy were called so
/// tests can prove the constant-work verify runs for unknown/disabled logins. "Hashing" is
/// a trivial reversible marker; verification compares the marker.
/// </summary>
internal sealed class FakePasswordHasher : IPasswordHasher
{
    public int VerifyCalls { get; private set; }

    public int VerifyDecoyCalls { get; private set; }

    /// <summary>When true, NeedsRehash returns true so the login rehash path runs.</summary>
    public bool RehashNeeded { get; set; }

    /// <summary>
    /// Fired inside NeedsRehash, i.e. AFTER the password verify but BEFORE the rehash UpdateHashAsync
    /// A test sets this to simulate a concurrent password change/reset landing in that exact
    /// window, so the rehash compare-and-swap must find the row changed and not clobber it.
    /// </summary>
    public Action? OnNeedsRehash { get; set; }

    public string Hash(string password) => $"hash:{password}";

    public bool Verify(string password, string encodedHash)
    {
        VerifyCalls++;
        return encodedHash == $"hash:{password}";
    }

    public bool NeedsRehash(string encodedHash)
    {
        OnNeedsRehash?.Invoke();
        return RehashNeeded;
    }

    public bool VerifyDecoy(string password)
    {
        VerifyDecoyCalls++;
        return false;
    }
}

/// <summary>Recording <see cref="IAuthEmailSender"/>.</summary>
internal sealed class FakeEmailSender : IAuthEmailSender
{
    public List<(string Email, string Token)> PasswordResets { get; } = [];

    public List<(string Email, string Token)> MagicLinks { get; } = [];

    public Task SendPasswordResetAsync(string email, string resetToken, CancellationToken cancellationToken = default)
    {
        PasswordResets.Add((email, resetToken));
        return Task.CompletedTask;
    }

    public Task SendMagicLinkAsync(string email, string magicLinkToken, CancellationToken cancellationToken = default)
    {
        MagicLinks.Add((email, magicLinkToken));
        return Task.CompletedTask;
    }
}

/// <summary>
/// In-memory <see cref="IAuthRateLimitStore"/>. Counts per (kind,key) and locks at the limit so
/// the 429 path is testable; <see cref="ForceLimited"/> trips the next check unconditionally.
/// </summary>
internal sealed class FakeRateLimitStore : IAuthRateLimitStore
{
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);

    public bool ForceLimited { get; set; }

    /// <summary>The IP bucket keys observed, for asserting which client IP the limiter used.</summary>
    public ConcurrentBag<string> IpKeysSeen { get; } = [];

    public Task<RateLimitResult> CheckAndIncrementAsync(RateLimitBucketKind kind, string key, int limit, TimeSpan window, TimeSpan lockout, CancellationToken cancellationToken = default)
    {
        if (kind == RateLimitBucketKind.Ip)
        {
            IpKeysSeen.Add(key);
        }

        if (ForceLimited)
        {
            return Task.FromResult(new RateLimitResult(true, limit, lockout));
        }

        var count = _counts.AddOrUpdate($"{kind}:{key}", 1, (_, c) => c + 1);
        var limited = count >= limit;
        return Task.FromResult(new RateLimitResult(limited, count, limited ? lockout : TimeSpan.Zero));
    }

    public Task ResetAccountAsync(string accountKey, CancellationToken cancellationToken = default)
    {
        _counts.TryRemove($"{RateLimitBucketKind.Account}:{accountKey}", out _);
        return Task.CompletedTask;
    }

    public Task<int> PruneAsync(TimeSpan retention, CancellationToken cancellationToken = default)
        => Task.FromResult(0);
}
