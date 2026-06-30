using Freeboard.Persistence.Auth;
using Freeboard.Persistence.System;
using Freeboard.TestInfrastructure;

namespace Freeboard.Persistence.Tests;

// Integration coverage for two store read paths that were previously unexercised against real
// MySQL: WebAuthn FindByCredentialId/ListByUser (BIGINT UNSIGNED -> long sign_count, CHAR(36) ->
// Guid aaguid, TINYINT(1) NULL -> bool? backup flags, VARBINARY -> byte[]) and RecoveryCode
// Consume (ValueTuple read). The driver-reported CLR types do not match WebAuthnCredentialRow's
// constructor, so Dapper cannot bind it directly; WebAuthnCredentialRowDto bridges them.
[Trait("Category", TestCategories.Integration)]
public sealed class WebAuthnAndRecoveryCodeReadPathTests
{
    private static async Task<MySqlTestDatabase> RequireDbAsync()
    {
        var db = await MySqlTestDatabase.TryCreateAsync();
        Skip.If(db is null, $"{MySqlTestDatabase.EnvVar} not set; skipping.");
        return db!;
    }

    private static async Task MigrateAsync(MySqlTestDatabase db) =>
        await new MySqlMigrationRunner(db.ConnectionFactory, typeof(IMigrationRunner).Assembly).ApplyPendingAsync();

    private static AuthCryptoOptions TestCrypto() => new()
    {
        PasswordSecrets = new Dictionary<int, byte[]> { [1] = Enumerable.Repeat((byte)0x11, 32).ToArray() },
        CurrentPasswordSecretVersion = 1,
        TokenKeys = new Dictionary<int, byte[]> { [1] = Enumerable.Repeat((byte)0x22, 32).ToArray() },
        CurrentTokenKeyVersion = 1,
        SecretProtectionKeys = new Dictionary<int, byte[]> { [1] = Enumerable.Repeat((byte)0x33, 32).ToArray() },
        CurrentSecretProtectionKeyVersion = 1,
    };

    private static async Task<string> CreateUserAsync(MySqlTestDatabase db, UlidFactory ulids, string email)
    {
        var users = new MySqlUserStore(db.ConnectionFactory, ulids);
        var u = await users.CreateAsync(new NewUser(email, "X", "admin"));
        return u.Id;
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task WebAuthnReadPathMaterializesBigIntUnsignedAndNullableBoolFlags()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var ulids = new UlidFactory();
        var store = new MySqlWebAuthnCredentialStore(db.ConnectionFactory, ulids);
        var userId = await CreateUserAsync(db, ulids, "wa@e.com");

        // sign_count near long.MaxValue still inside BIGINT UNSIGNED range; flags set so TINYINT(1)
        // NULL -> bool? exercises the true/false (not just NULL) path.
        const long bigCount = 9_000_000_000_000_000_000L;
        var aaguid = "12345678-1234-1234-1234-1234567890ab";
        var added = await store.AddAsync(new NewWebAuthnCredential(
            userId, [10, 20, 30], [40, 50, 60], bigCount, [7, 8, 9],
            aaguid, "usb,nfc", "public-key", true, false, "my key"));

        // These two are the UNCOVERED read paths.
        var byId = await store.FindByCredentialIdAsync([10, 20, 30]);
        Assert.NotNull(byId);
        Assert.Equal(bigCount, byId!.SignCount);
        Assert.Equal(true, byId.IsBackupEligible);
        Assert.Equal(false, byId.IsBackedUp);
        Assert.Equal([10, 20, 30], byId.CredentialId);
        Assert.Equal([40, 50, 60], byId.PublicKey);
        Assert.Equal([7, 8, 9], byId.UserHandle);
        Assert.Equal("my key", byId.Nickname);
        Assert.Equal(aaguid, byId.Aaguid);

        var list = await store.ListByUserAsync(userId);
        Assert.Single(list);
        Assert.Equal(bigCount, list[0].SignCount);
        Assert.Equal(added.Id, list[0].Id);

        // Null flags branch.
        var added2 = await store.AddAsync(new NewWebAuthnCredential(
            userId, [11, 21], [41, 51], 0, [1], null, null, null, null, null, null));
        var byId2 = await store.FindByCredentialIdAsync([11, 21]);
        Assert.NotNull(byId2);
        Assert.Null(byId2!.IsBackupEligible);
        Assert.Null(byId2.IsBackedUp);
        Assert.Equal(added2.Id, byId2.Id);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task RecoveryCodeConsumeReadPathMaterializes()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var ulids = new UlidFactory();
        var hasher = new HmacTokenHasher(TestCrypto());
        var store = new MySqlRecoveryCodeStore(db.ConnectionFactory, ulids, hasher);
        var userId = await CreateUserAsync(db, ulids, "rc@e.com");

        var codes = await store.RegenerateAsync(userId, 5);
        Assert.Equal(5, codes.Count);

        // Consume exercises the (string Id, byte[] CodeHash, int TokenKeyVersion) ValueTuple read.
        Assert.True(await store.ConsumeAsync(userId, codes[0]));
        Assert.False(await store.ConsumeAsync(userId, codes[0])); // single-use
        Assert.Equal(4, await store.CountRemainingAsync(userId));
    }
}
