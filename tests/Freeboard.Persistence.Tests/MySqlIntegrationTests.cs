using System.Web;
using Dapper;
using Freeboard.Core.GitOps;
using Freeboard.Persistence;
using Freeboard.Persistence.Auth;
using Freeboard.Persistence.GitOps;
using Freeboard.Persistence.System;
using Freeboard.TestInfrastructure;
using MySqlConnector;
using OtpNet;

namespace Freeboard.Persistence.Tests;

/// <summary>
/// Integration tests against a real MySQL discovered via FREEBOARD_TEST_DB. Each test
/// SKIPS cleanly (not fails) when the env var is absent. Each gets a fresh throwaway
/// database.
/// </summary>
public sealed class MySqlIntegrationTests
{
    private static async Task<MySqlTestDatabase> RequireDbAsync()
    {
        var db = await MySqlTestDatabase.TryCreateAsync();
        Skip.If(db is null, $"{MySqlTestDatabase.EnvVar} not set; skipping MySQL integration test.");
        return db!;
    }

    private static MySqlMigrationRunner RealRunner(MySqlTestDatabase db) =>
        new(db.ConnectionFactory, typeof(IMigrationRunner).Assembly);

    private static async Task MigrateAsync(MySqlTestDatabase db) =>
        await RealRunner(db).ApplyPendingAsync();

    private static GitOpsConfig Config(
        IEnumerable<Standard> standards,
        IEnumerable<Control> controls,
        IEnumerable<Scope> scopes) => new()
        {
            Standards = standards.ToList(),
            Controls = controls.ToList(),
            Scopes = scopes.ToList(),
        };

    private static Standard Std(string id, string title = "T", string apiVersion = "v1") =>
        new() { Id = id, Title = title, ApiVersion = apiVersion };

    private static Control Ctrl(string id, string[] mapsTo, string title = "T", string apiVersion = "v1") =>
        new() { Id = id, Title = title, ApiVersion = apiVersion, MapsTo = [.. mapsTo] };

    private static Scope Scp(string id, string[] controls, string title = "T", string apiVersion = "v1") =>
        new() { Id = id, Title = title, ApiVersion = apiVersion, Controls = [.. controls] };

    [SkippableFact]
    public async Task MigrateEmptySchemaCreatesAllTablesWithBinaryCollation()
    {
        await using var db = await RequireDbAsync();

        var applied = await RealRunner(db).ApplyPendingAsync();
        Assert.Contains("001_initial_schema", applied);

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var tables = (await conn.QueryAsync<string>(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = DATABASE();"))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var t in new[] { "standards", "controls", "scopes", "control_standards", "scope_controls", "schema_migrations" })
        {
            Assert.Contains(t, tables);
        }

        // id columns are binary-collated.
        var collation = await conn.ExecuteScalarAsync<string>(
            "SELECT collation_name FROM information_schema.columns "
            + "WHERE table_schema = DATABASE() AND table_name = 'standards' AND column_name = 'id';");
        Assert.Equal("utf8mb4_bin", collation);

        // FK present on a join table.
        var fkCount = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.table_constraints "
            + "WHERE table_schema = DATABASE() AND table_name = 'control_standards' AND constraint_type = 'FOREIGN KEY';");
        Assert.True(fkCount >= 2);
    }

    [SkippableFact]
    public async Task GetStateOnEmptyDbReportsAllPendingAndCreatesNoTables()
    {
        await using var db = await RequireDbAsync();
        var runner = RealRunner(db);

        var state = await runner.GetStateAsync();
        Assert.False(state.IsCurrent);
        Assert.Contains("001_initial_schema", state.Pending);

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var tableCount = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE();");
        Assert.Equal(0, tableCount);

        await runner.ApplyPendingAsync();
        Assert.True((await runner.GetStateAsync()).IsCurrent);
    }

    [SkippableFact]
    public async Task FailedMigrationLeavesVersionUnrecordedAndIsReAttempted()
    {
        await using var db = await RequireDbAsync();

        // Test assembly: 001_first, 002_second, 010_tenth apply; 020_broken fails.
        var runner = new MySqlMigrationRunner(db.ConnectionFactory, typeof(MySqlIntegrationTests).Assembly);

        var ex = await Assert.ThrowsAsync<MigrationException>(() => runner.ApplyPendingAsync());
        Assert.Contains("020_broken", ex.Message);

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var recorded = (await conn.QueryAsync<string>("SELECT version FROM schema_migrations;")).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("001_first", recorded);
        Assert.Contains("010_tenth", recorded);
        Assert.DoesNotContain("020_broken", recorded);

        // Re-run re-attempts the broken one (and still fails the same way).
        await Assert.ThrowsAsync<MigrationException>(() => runner.ApplyPendingAsync());
    }

    [SkippableFact]
    public async Task RecordedButMissingMigrationFailsLoudly()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            "INSERT INTO schema_migrations (version, checksum, applied_at) VALUES ('999_gone', 'x', NOW(6));");

        var ex = await Assert.ThrowsAsync<MigrationException>(() => RealRunner(db).ApplyPendingAsync());
        Assert.Contains("missing", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task ChecksumMismatchOfAppliedMigrationFailsLoudly()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            "UPDATE schema_migrations SET checksum = REPEAT('0', 64) WHERE version = '001_initial_schema';");

        var ex = await Assert.ThrowsAsync<MigrationException>(() => RealRunner(db).ApplyPendingAsync());
        Assert.Contains("checksum", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task SyncRoundTripsCountsAndCrossRefs()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        var importer = new MySqlGitOpsImporter(db.ConnectionFactory);
        var store = new MySqlComplianceStore(db.ConnectionFactory);

        await importer.ImportAsync(Config(
            [Std("std-a"), Std("std-b")],
            [Ctrl("ctrl-a", ["std-a", "std-b"])],
            [Scp("scope-a", ["ctrl-a"])]));

        var counts = await store.GetCountsAsync();
        Assert.Equal(new ComplianceCounts(2, 1, 1), counts);

        var control = Assert.Single(await store.GetControlsAsync());
        Assert.Equal(["std-a", "std-b"], control.MapsTo);

        var scope = Assert.Single(await store.GetScopesAsync());
        Assert.Equal(["ctrl-a"], scope.Controls);
    }

    [SkippableFact]
    public async Task ResyncUpdatesByIdAndRemovesDroppedIds()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var importer = new MySqlGitOpsImporter(db.ConnectionFactory);
        var store = new MySqlComplianceStore(db.ConnectionFactory);

        await importer.ImportAsync(Config(
            [Std("std-a", "Old"), Std("std-b", "Keep B")],
            [], []));

        await importer.ImportAsync(Config(
            [Std("std-a", "New title")],
            [], []));

        var standards = await store.GetStandardsAsync();
        var only = Assert.Single(standards);
        Assert.Equal("std-a", only.Id);
        Assert.Equal("New title", only.Title);
    }

    [SkippableFact]
    public async Task ResyncPreservesCreatedAtAndAdvancesUpdatedAt()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var importer = new MySqlGitOpsImporter(db.ConnectionFactory);

        await importer.ImportAsync(Config([Std("std-a", "v1")], [], []));

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var (created1, updated1) = await conn.QuerySingleAsync<(DateTime Created, DateTime Updated)>(
            "SELECT created_at AS Created, updated_at AS Updated FROM standards WHERE id = 'std-a';");

        await Task.Delay(20);
        await importer.ImportAsync(Config([Std("std-a", "v2")], [], []));

        var (created2, updated2) = await conn.QuerySingleAsync<(DateTime Created, DateTime Updated)>(
            "SELECT created_at AS Created, updated_at AS Updated FROM standards WHERE id = 'std-a';");

        Assert.Equal(created1, created2);
        Assert.True(updated2 >= updated1);
    }

    [SkippableFact]
    public async Task ResyncUpdatesStoredApiVersion()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var importer = new MySqlGitOpsImporter(db.ConnectionFactory);

        await importer.ImportAsync(Config([Std("std-a", apiVersion: "freeboard.io/v1alpha1")], [], []));
        await importer.ImportAsync(Config([Std("std-a", apiVersion: "freeboard.io/v1beta1")], [], []));

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var apiVersion = await conn.ExecuteScalarAsync<string>(
            "SELECT api_version FROM standards WHERE id = 'std-a';");
        Assert.Equal("freeboard.io/v1beta1", apiVersion);
    }

    [SkippableFact]
    public async Task FkSafeDropOfReferencedStandardSucceeds()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var importer = new MySqlGitOpsImporter(db.ConnectionFactory);
        var store = new MySqlComplianceStore(db.ConnectionFactory);

        // Old state: ctrl-a maps to std-a and std-b.
        await importer.ImportAsync(Config(
            [Std("std-a"), Std("std-b")],
            [Ctrl("ctrl-a", ["std-a", "std-b"])],
            []));

        // New config drops std-b and re-maps ctrl-a to std-a only. Must not FK-violate.
        await importer.ImportAsync(Config(
            [Std("std-a")],
            [Ctrl("ctrl-a", ["std-a"])],
            []));

        Assert.Single(await store.GetStandardsAsync());
        Assert.Equal(["std-a"], Assert.Single(await store.GetControlsAsync()).MapsTo);
    }

    [SkippableFact]
    public async Task CaseDistinctIdsRemainDistinctRows()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var importer = new MySqlGitOpsImporter(db.ConnectionFactory);
        var store = new MySqlComplianceStore(db.ConnectionFactory);

        await importer.ImportAsync(Config([Std("ctrl-a"), Std("CTRL-A")], [], []));

        Assert.Equal(2, (await store.GetStandardsAsync()).Count);
    }

    // A locked rate-limit bucket stays locked across a window rollover. The pure decision
    // is unit-tested in RateLimitDecisionTests; this asserts the SQL store preserves the lock.
    //
    // The window is long enough that the first two attempts land in the SAME window (tripping the
    // lock at the limit), yet short enough to elapse before the third attempt, while the 30-minute
    // lockout still holds. That is the actual rollover-survival case. A sub-millisecond window would
    // be non-deterministic against a real DB: the two attempts could straddle the window and the
    // bucket roll over before it locks.
    [SkippableFact]
    public async Task RateLimitLockSurvivesWindowRollover()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var store = new Freeboard.Persistence.Auth.MySqlAuthRateLimitStore(db.ConnectionFactory);

        var window = TimeSpan.FromMilliseconds(200);
        var lockout = TimeSpan.FromMinutes(30);

        // Two attempts in the same window reach the limit (2) and lock the bucket.
        await store.CheckAndIncrementAsync(
            Freeboard.Persistence.Auth.RateLimitBucketKind.Account, "lock@example.com", 2, window, lockout);
        var second = await store.CheckAndIncrementAsync(
            Freeboard.Persistence.Auth.RateLimitBucketKind.Account, "lock@example.com", 2, window, lockout);
        Assert.True(second.Limited);

        // The 200ms window has now elapsed, but the lock (30 min) must still hold: a rollover must
        // never clear a live lock.
        await Task.Delay(400);
        var third = await store.CheckAndIncrementAsync(
            Freeboard.Persistence.Auth.RateLimitBucketKind.Account, "lock@example.com", 2, window, lockout);
        Assert.True(third.Limited);
    }

    // Two concurrent first attempts on a fresh bucket must not both land count 1.
    [SkippableFact]
    public async Task RateLimitConcurrentFirstAttemptsSerialize()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var store = new Freeboard.Persistence.Auth.MySqlAuthRateLimitStore(db.ConnectionFactory);

        var window = TimeSpan.FromMinutes(10);
        var lockout = TimeSpan.FromMinutes(10);

        var a = store.CheckAndIncrementAsync(
            Freeboard.Persistence.Auth.RateLimitBucketKind.Ip, "10.0.0.1", 100, window, lockout);
        var b = store.CheckAndIncrementAsync(
            Freeboard.Persistence.Auth.RateLimitBucketKind.Ip, "10.0.0.1", 100, window, lockout);
        var results = await Task.WhenAll(a, b);

        // One landed 1 and the other 2 - never two 1s.
        var counts = results.Select(r => r.AttemptCount).OrderBy(c => c).ToArray();
        Assert.Equal([1, 2], counts);
    }

    // The sign-counter update is rejected atomically in SQL on a positive regression.
    [SkippableFact]
    public async Task WebAuthnSignCountRegressionRejectedBySqlGuard()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        var ulids = new Freeboard.Persistence.Auth.UlidFactory();
        var store = new Freeboard.Persistence.Auth.MySqlWebAuthnCredentialStore(db.ConnectionFactory, ulids);

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var userId = ulids.NewId();
        await conn.ExecuteAsync(
            "INSERT INTO users (id, email, email_normalized, name, global_role, enabled, force_password_reset, mfa_enabled, created_at, updated_at) "
            + "VALUES (@Id, 'w@e.com', 'w@e.com', 'W', 'admin', 1, 0, 0, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));",
            new { Id = userId });

        var cred = await store.AddAsync(new Freeboard.Persistence.Auth.NewWebAuthnCredential(
            userId, [1, 2, 3], [4, 5, 6], 5, [7, 8], null, null, null, null, null, null));

        Assert.True(await store.UpdateSignCountAsync(cred.Id, 6, DateTime.UtcNow)); // strict increase
        Assert.False(await store.UpdateSignCountAsync(cred.Id, 6, DateTime.UtcNow)); // positive regression rejected
        Assert.True(await store.UpdateSignCountAsync(cred.Id, 0, DateTime.UtcNow)); // synced 0 accepted
    }

    // The combined password-update + session-revocation is one transaction. Verifies the
    // hash is updated, force_password_reset is flipped, and only non-kept sessions are revoked.
    [SkippableFact]
    public async Task UpdateHashAndRevokeSessionsIsAtomic()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        var ulids = new Freeboard.Persistence.Auth.UlidFactory();
        var credentials = new Freeboard.Persistence.Auth.MySqlPasswordCredentialStore(db.ConnectionFactory);
        var sessions = new Freeboard.Persistence.Auth.MySqlSessionStore(db.ConnectionFactory, ulids);
        var users = new Freeboard.Persistence.Auth.MySqlUserStore(db.ConnectionFactory, ulids);

        var user = await users.CreateAsync(new Freeboard.Persistence.Auth.NewUser("u@e.com", "U", "admin"));
        await credentials.SetAsync(user.Id, "old-hash", 1);

        var keep = await sessions.CreateAsync(
            user.Id, [1, 1], 1, Freeboard.Persistence.Auth.SessionAuthState.ForceResetLimited, 1, DateTime.UtcNow.AddHours(1));
        var revoke = await sessions.CreateAsync(
            user.Id, [2, 2], 1, Freeboard.Persistence.Auth.SessionAuthState.Full, 1, DateTime.UtcNow.AddHours(1));

        // Keep the "keep" session, flip force_password_reset to false, upgrade it to full, revoke
        // the other. The epoch bumps to 2 and the kept session's stored epoch is stamped to 2.
        var newVersion = await credentials.UpdateHashAndRevokeSessionsAsync(
            user.Id, "new-hash", 2, keepSessionId: keep.Id, setForcePasswordReset: false, upgradeKeptSessionToFull: true);
        Assert.Equal(2, newVersion);

        var cred = await credentials.GetAsync(user.Id);
        Assert.Equal("new-hash", cred!.PasswordHash);
        Assert.Equal(2, cred.SecretVersion);
        Assert.Equal(2, cred.CredentialVersion);

        var keptRow = await sessions.GetByIdAsync(keep.Id);
        Assert.NotNull(keptRow);
        Assert.Equal(2, keptRow!.CredentialVersion); // stamped to the new epoch
        Assert.Equal(Freeboard.Persistence.Auth.SessionAuthState.Full, keptRow.AuthState); // upgraded
        Assert.Null(await sessions.GetByIdAsync(revoke.Id));

        var refreshed = await users.GetByIdAsync(user.Id);
        Assert.False(refreshed!.ForcePasswordReset);

        // keepSessionId = null revokes ALL; epoch bumps to 3.
        var v3 = await credentials.UpdateHashAndRevokeSessionsAsync(
            user.Id, "newer-hash", 2, keepSessionId: null, setForcePasswordReset: true, upgradeKeptSessionToFull: false);
        Assert.Equal(3, v3);
        Assert.Empty(await sessions.ListByUserAsync(user.Id));
        Assert.True((await users.GetByIdAsync(user.Id))!.ForcePasswordReset);
    }

    // The mfa_login_challenges.credential_version column round-trips, and the
    // atomic magic-link verify-and-consume is single-use and bound to the challenge user.
    [SkippableFact]
    public async Task MfaChallengeCredentialVersionAndMagicLinkVerifyAndConsume()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        var ulids = new Freeboard.Persistence.Auth.UlidFactory();
        var users = new Freeboard.Persistence.Auth.MySqlUserStore(db.ConnectionFactory, ulids);
        var crypto = new Freeboard.Persistence.Auth.AuthCryptoOptions
        {
            PasswordSecrets = new Dictionary<int, byte[]> { [1] = new byte[32] },
            CurrentPasswordSecretVersion = 1,
            TokenKeys = new Dictionary<int, byte[]> { [1] = new byte[32] },
            CurrentTokenKeyVersion = 1,
            SecretProtectionKeys = new Dictionary<int, byte[]> { [1] = new byte[32] },
            CurrentSecretProtectionKeyVersion = 1,
        };
        var hasher = new Freeboard.Persistence.Auth.HmacTokenHasher(crypto);
        var challenges = new Freeboard.Persistence.Auth.MySqlMfaChallengeStore(db.ConnectionFactory, ulids, hasher);

        var userA = await users.CreateAsync(new Freeboard.Persistence.Auth.NewUser("a@e.com", "A", "member"));
        var userB = await users.CreateAsync(new Freeboard.Persistence.Auth.NewUser("b@e.com", "B", "member"));

        // Create a challenge stamped with credential epoch 7 and assert it round-trips.
        var minted = await challenges.CreateAsync(
            userA.Id, 7, "magic_link", null, DateTime.UtcNow.AddMinutes(10));
        Assert.Equal(7, minted.Row.CredentialVersion);
        var found = await challenges.FindByTokenAsync(minted.Token, DateTime.UtcNow);
        Assert.Equal(7, found!.CredentialVersion);

        // Set a magic-link token on the challenge.
        var link = hasher.MintPrefixless();
        Assert.True(await challenges.SetMagicLinkAsync(
            minted.Row.Id, link.Hash, link.KeyVersion, DateTime.UtcNow.AddMinutes(10), 3));

        // User B cannot consume user A's challenge.
        Assert.False(await challenges.VerifyAndConsumeMagicLinkAsync(minted.Row.Id, userB.Id, link.Token, DateTime.UtcNow));

        // User A consumes it once...
        Assert.True(await challenges.VerifyAndConsumeMagicLinkAsync(minted.Row.Id, userA.Id, link.Token, DateTime.UtcNow));
        // ...and a replay fails (single-use).
        Assert.False(await challenges.VerifyAndConsumeMagicLinkAsync(minted.Row.Id, userA.Id, link.Token, DateTime.UtcNow));
    }

    // Concurrent sudo magic-link sends with NO pre-existing challenge must converge on ONE row
    // (the (user_id, sudo_dedupe_key) unique key + INSERT ... ON DUPLICATE KEY UPDATE), so the
    // per-challenge re-send cap holds instead of being multiplied by the race.
    [SkippableFact]
    public async Task SudoMagicLinkFindOrCreateIsAtomicUnderConcurrency()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        var ulids = new Freeboard.Persistence.Auth.UlidFactory();
        var users = new Freeboard.Persistence.Auth.MySqlUserStore(db.ConnectionFactory, ulids);
        var crypto = new Freeboard.Persistence.Auth.AuthCryptoOptions
        {
            PasswordSecrets = new Dictionary<int, byte[]> { [1] = new byte[32] },
            CurrentPasswordSecretVersion = 1,
            TokenKeys = new Dictionary<int, byte[]> { [1] = new byte[32] },
            CurrentTokenKeyVersion = 1,
            SecretProtectionKeys = new Dictionary<int, byte[]> { [1] = new byte[32] },
            CurrentSecretProtectionKeyVersion = 1,
        };
        var hasher = new Freeboard.Persistence.Auth.HmacTokenHasher(crypto);
        var challenges = new Freeboard.Persistence.Auth.MySqlMfaChallengeStore(db.ConnectionFactory, ulids, hasher);

        var user = await users.CreateAsync(new Freeboard.Persistence.Auth.NewUser("c@e.com", "C", "member"));
        const int maxSends = 3;
        var now = DateTime.UtcNow;

        // 12 concurrent first sends. Each mints its own magic-link token; the store decides which
        // land. Exactly maxSends should be accepted and exactly one challenge row should exist.
        var tasks = Enumerable.Range(0, 12).Select(_ =>
        {
            var link = hasher.MintPrefixless();
            return challenges.FindOrCreateSudoMagicLinkAsync(
                user.Id, 1, link.Hash, link.KeyVersion,
                now.AddMinutes(10), now.AddMinutes(10), maxSends, now);
        });
        var results = await Task.WhenAll(tasks);

        // The cap holds atomically: all callers converge on ONE challenge row and magic_link_sends
        // reaches exactly maxSends - the race does not multiply it. How many callers report Sent is
        // timing-dependent: the stored magic-link token is last-writer-wins, so an earlier under-cap
        // sender can be overwritten before it reads its own token back. It is always between 1 and
        // maxSends, never more - so at most maxSends links are ever emailed.
        var accepted = results.Count(r => r.Sent);
        Assert.InRange(accepted, 1, maxSends);
        Assert.Single(results.Select(r => r.ChallengeId).Distinct());

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var row = await conn.QuerySingleAsync<(long Count, int Sends)>(
            "SELECT COUNT(*) AS Count, COALESCE(MAX(magic_link_sends), 0) AS Sends FROM mfa_login_challenges "
            + "WHERE user_id = @UserId AND sudo_dedupe_key = 'magic_link';",
            new { UserId = user.Id });
        Assert.Equal(1, row.Count);
        Assert.Equal(maxSends, row.Sends);
    }

    // Test crypto with fixed 32-byte keys (>= 32 required by AuthKeyMaterial.Validate). Distinct
    // bytes per key set so a swapped key would not silently pass.
    private static AuthCryptoOptions TestCrypto() => new()
    {
        PasswordSecrets = new Dictionary<int, byte[]> { [1] = Enumerable.Repeat((byte)0x11, 32).ToArray() },
        CurrentPasswordSecretVersion = 1,
        TokenKeys = new Dictionary<int, byte[]> { [1] = Enumerable.Repeat((byte)0x22, 32).ToArray() },
        CurrentTokenKeyVersion = 1,
        SecretProtectionKeys = new Dictionary<int, byte[]> { [1] = Enumerable.Repeat((byte)0x33, 32).ToArray() },
        CurrentSecretProtectionKeyVersion = 1,
    };

    // Reset-token single-use: the conditional UPDATE ... WHERE used_at IS NULL means a reset
    // token consumes exactly once; a replay returns null even though the token is well-formed.
    [SkippableFact]
    public async Task ResetTokenIsSingleUse()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        var ulids = new UlidFactory();
        var hasher = new HmacTokenHasher(TestCrypto());
        var users = new MySqlUserStore(db.ConnectionFactory, ulids);
        var resets = new MySqlPasswordResetStore(db.ConnectionFactory, ulids, hasher);

        var user = await users.CreateAsync(new NewUser("reset@e.com", "R", "admin"));
        var minted = await resets.CreateAsync(user.Id, DateTime.UtcNow.AddMinutes(10));

        // First consume wins and returns the owning user; the replay finds used_at set and fails.
        Assert.Equal(user.Id, await resets.ConsumeAsync(minted.Token, DateTime.UtcNow));
        Assert.Null(await resets.ConsumeAsync(minted.Token, DateTime.UtcNow));
    }

    // TOTP encrypted at rest + replay: the stored secret_ciphertext is not the plaintext
    // (AES-256-GCM at rest), a valid code verifies, and a re-verify of the SAME code in the SAME
    // step is rejected by the atomic last_time_step advance.
    [SkippableFact]
    public async Task TotpSecretEncryptedAtRestAndReplayRejected()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        var ulids = new UlidFactory();
        var protector = new AesGcmSecretProtector(TestCrypto());
        var users = new MySqlUserStore(db.ConnectionFactory, ulids);
        var totp = new MySqlTotpStore(db.ConnectionFactory, protector);

        var user = await users.CreateAsync(new NewUser("totp@e.com", "T", "admin"));
        var enrollment = await totp.EnrollAsync(user.Id, "totp@e.com", "Freeboard");

        // Recover the plaintext secret from the one-time provisioning URI to (a) prove the stored
        // ciphertext is not the plaintext and (b) compute a valid code.
        var secretBase32 = HttpUtility.ParseQueryString(new Uri(enrollment.ProvisioningUri).Query)["secret"];
        Assert.False(string.IsNullOrEmpty(secretBase32));
        var secret = Base32Encoding.ToBytes(secretBase32);

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var ciphertext = await conn.ExecuteScalarAsync<byte[]>(
            "SELECT secret_ciphertext FROM totp_credentials WHERE user_id = @UserId;",
            new { UserId = user.Id });
        Assert.NotNull(ciphertext);
        Assert.NotEqual(secret, ciphertext); // encrypted at rest, not the bare secret

        var code = new Totp(secret).ComputeTotp();
        Assert.True(await totp.ActivateAsync(user.Id, code));        // confirms + advances the step
        Assert.False(await totp.VerifyAsync(user.Id, code));         // replay within the same step rejected
    }

    // Rotating an already-confirmed TOTP secret stages the replacement and keeps the old secret
    // live and confirmed until the new one is activated, so an abandoned rotation cannot lock the
    // user out. Activation must prove the NEW secret; on success it is promoted to live and the
    // pending slot is cleared.
    [SkippableFact]
    public async Task TotpRotationPreservesConfirmedSecretUntilNewOneActivated()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        var ulids = new UlidFactory();
        var protector = new AesGcmSecretProtector(TestCrypto());
        var users = new MySqlUserStore(db.ConnectionFactory, ulids);
        var totp = new MySqlTotpStore(db.ConnectionFactory, protector);

        static byte[] SecretFromUri(string uri) =>
            Base32Encoding.ToBytes(HttpUtility.ParseQueryString(new Uri(uri).Query)["secret"]);

        var user = await users.CreateAsync(new NewUser("rotate@e.com", "R", "admin"));

        // Enroll and confirm the first secret.
        var first = await totp.EnrollAsync(user.Id, "rotate@e.com", "Freeboard");
        var secretA = SecretFromUri(first.ProvisioningUri);
        Assert.True(await totp.ActivateAsync(user.Id, new Totp(secretA).ComputeTotp()));

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var liveAfterActivate = await conn.ExecuteScalarAsync<byte[]>(
            "SELECT secret_ciphertext FROM totp_credentials WHERE user_id = @UserId;", new { UserId = user.Id });

        // Rotate: enrolling a replacement stages it as pending and leaves the confirmed secret live.
        var second = await totp.EnrollAsync(user.Id, "rotate@e.com", "Freeboard");
        var secretB = SecretFromUri(second.ProvisioningUri);

        var during = await conn.QuerySingleAsync<(byte[] Live, byte[]? Pending, DateTime? Confirmed)>(
            "SELECT secret_ciphertext AS Live, pending_secret_ciphertext AS Pending, confirmed_at AS Confirmed "
            + "FROM totp_credentials WHERE user_id = @UserId;", new { UserId = user.Id });
        Assert.Equal(liveAfterActivate, during.Live);   // live secret untouched by enrollment
        Assert.NotNull(during.Pending);                  // replacement staged
        Assert.NotEqual(during.Live, during.Pending);
        Assert.NotNull(during.Confirmed);                // still a confirmed factor
        Assert.True(await totp.IsConfirmedAsync(user.Id));

        // The old secret's code cannot promote the pending one: activation must prove the new secret.
        Assert.False(await totp.ActivateAsync(user.Id, new Totp(secretA).ComputeTotp()));

        // Activating with the new secret promotes it to live and clears the pending slot.
        Assert.True(await totp.ActivateAsync(user.Id, new Totp(secretB).ComputeTotp()));
        var after = await conn.QuerySingleAsync<(byte[] Live, byte[]? Pending, DateTime? Confirmed)>(
            "SELECT secret_ciphertext AS Live, pending_secret_ciphertext AS Pending, confirmed_at AS Confirmed "
            + "FROM totp_credentials WHERE user_id = @UserId;", new { UserId = user.Id });
        Assert.Null(after.Pending);
        Assert.NotNull(after.Confirmed);
        Assert.NotEqual(during.Live, after.Live);        // live secret is now the rotated one
    }

    // Concurrent single-admin bootstrap: N concurrent TryBootstrapAdminAsync calls
    // against one fresh DB create EXACTLY ONE admin. The bootstrap_marker sentinel PK collision
    // makes the losers return null, and the users table ends with exactly one row.
    [SkippableFact]
    public async Task ConcurrentBootstrapCreatesExactlyOneAdmin()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        var ulids = new UlidFactory();
        var users = new MySqlUserStore(db.ConnectionFactory, ulids);

        const int callers = 12;
        var tasks = Enumerable.Range(0, callers).Select(_ =>
            users.TryBootstrapAdminAsync(new NewUser("admin@e.com", "Admin", "admin"), "hash", 1));
        var results = await Task.WhenAll(tasks);

        Assert.Single(results, r => r is not null);

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM users;"));
    }

    // MFA challenge 5-attempt auto-consume: RegisterFailedAttemptAsync auto-consumes the row
    // on the 5th failure (consumed_at set in the same conditional UPDATE), after which the
    // challenge can no longer be found by token or consumed.
    [SkippableFact]
    public async Task MfaChallengeAutoConsumesOnFifthFailedAttempt()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        var ulids = new UlidFactory();
        var hasher = new HmacTokenHasher(TestCrypto());
        var users = new MySqlUserStore(db.ConnectionFactory, ulids);
        var challenges = new MySqlMfaChallengeStore(db.ConnectionFactory, ulids, hasher);

        var user = await users.CreateAsync(new NewUser("mfa@e.com", "M", "member"));
        var minted = await challenges.CreateAsync(user.Id, 1, "totp", null, DateTime.UtcNow.AddMinutes(10));

        const int cap = 5;
        // First four failures leave attempts under the cap and do not consume.
        for (var i = 1; i < cap; i++)
        {
            Assert.False(await challenges.RegisterFailedAttemptAsync(minted.Row.Id, cap));
        }
        // The 5th failure reaches the cap and auto-consumes the row.
        Assert.True(await challenges.RegisterFailedAttemptAsync(minted.Row.Id, cap));

        // Consumed: it can no longer be found by token, and an explicit consume finds nothing to do.
        Assert.Null(await challenges.FindByTokenAsync(minted.Token, DateTime.UtcNow));
        Assert.False(await challenges.ConsumeAsync(minted.Row.Id, DateTime.UtcNow));
    }
}
