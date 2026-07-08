using Dapper;
using Freeboard.Persistence;
using Freeboard.Persistence.Auth;
using Freeboard.Persistence.System;
using Freeboard.TestInfrastructure;
using MySqlConnector;

namespace Freeboard.Persistence.Tests;

/// <summary>
/// Integration tests for the collector-credential schema (migration 014) and
/// <see cref="MySqlCollectorCredentialStore"/> against a real MySQL discovered via FREEBOARD_TEST_DB.
/// Each test SKIPS cleanly when the env var is absent.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class CollectorCredentialIntegrationTests
{
    private static readonly byte[] Hash = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    private static async Task<MySqlTestDatabase> RequireDbAsync()
    {
        var db = await MySqlTestDatabase.TryCreateAsync();
        Skip.If(db is null, $"{MySqlTestDatabase.EnvVar} not set; skipping MySQL integration test.");
        return db!;
    }

    private static async Task MigrateAsync(MySqlTestDatabase db) =>
        await new MySqlMigrationRunner(db.ConnectionFactory, typeof(IMigrationRunner).Assembly).ApplyPendingAsync();

    /// <summary>Seeds a control and a collector so the credential FK has a target. Returns the collector id.</summary>
    private static async Task<string> SeedCollectorAsync(MySqlConnection conn, string collectorId = "col-1")
    {
        await conn.ExecuteAsync(
            "INSERT INTO controls (id, api_version, title, created_at, updated_at) "
            + "VALUES ('ctrl-1', 'v1', 'C', NOW(6), NOW(6));");
        await conn.ExecuteAsync(
            "INSERT INTO evidence_collectors "
            + "(id, api_version, title, control_id, vendor_id, type, frequency, threshold, config, created_at, updated_at) "
            + "VALUES (@Id, 'v1', 'Collector', 'ctrl-1', NULL, 'integration', 'daily', NULL, NULL, NOW(6), NOW(6));",
            new { Id = collectorId });
        return collectorId;
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task MigrationCreatesTableUniqueHashAndCollectorFk()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var tables = (await conn.QueryAsync<string>(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = DATABASE();"))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("collector_credentials", tables);

        // token_hash is unique.
        var uniqueCols = (await conn.QueryAsync<string>(
            "SELECT column_name FROM information_schema.statistics "
            + "WHERE table_schema = DATABASE() AND table_name = 'collector_credentials' "
            + "AND index_name = 'ux_collector_credentials_token_hash' AND non_unique = 0;")).ToArray();
        Assert.Equal(["token_hash"], uniqueCols);

        // The credential FK references evidence_collectors.
        var fk = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.key_column_usage "
            + "WHERE table_schema = DATABASE() AND table_name = 'collector_credentials' "
            + "AND referenced_table_name = 'evidence_collectors';");
        Assert.Equal(1, fk);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task IssueFindRevokeAndTouchRoundTrip()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var collectorId = await SeedCollectorAsync(conn);

        var store = new MySqlCollectorCredentialStore(db.ConnectionFactory, new UlidFactory());

        var id = await store.IssueAsync(collectorId, Hash, tokenKeyVersion: 3, expiresAt: null);
        Assert.False(string.IsNullOrEmpty(id));

        var found = await store.FindByTokenHashAsync(Hash);
        Assert.NotNull(found);
        Assert.Equal(id, found!.Id);
        Assert.Equal(collectorId, found.CollectorId);
        Assert.Equal(3, found.TokenKeyVersion);
        Assert.Null(found.RevokedAt);
        Assert.Null(found.LastSeenAt);

        var seenAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        Assert.True(await store.TouchLastSeenAsync(id, seenAt));
        Assert.Equal(seenAt, (await store.FindByTokenHashAsync(Hash))!.LastSeenAt);

        // Revoke is scoped to the owning collector; a wrong collector does not revoke.
        Assert.False(await store.RevokeAsync("other", id));
        Assert.True(await store.RevokeAsync(collectorId, id));
        Assert.NotNull((await store.FindByTokenHashAsync(Hash))!.RevokedAt);
        // A second revoke is a no-op (already revoked).
        Assert.False(await store.RevokeAsync(collectorId, id));
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task DeletingCollectorCascadesCredential()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var collectorId = await SeedCollectorAsync(conn);

        var store = new MySqlCollectorCredentialStore(db.ConnectionFactory, new UlidFactory());
        await store.IssueAsync(collectorId, Hash, tokenKeyVersion: 1, expiresAt: null);
        Assert.NotNull(await store.FindByTokenHashAsync(Hash));

        // A credential is live config, not history: removing the collector cascades it away.
        await conn.ExecuteAsync("DELETE FROM evidence_collectors WHERE id = @Id;", new { Id = collectorId });

        Assert.Null(await store.FindByTokenHashAsync(Hash));
    }
}
