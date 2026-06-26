using Dapper;
using Freeboard.Core.GitOps;
using Freeboard.Persistence;
using Freeboard.Persistence.GitOps;
using Freeboard.Persistence.System;
using Freeboard.TestInfrastructure;
using MySqlConnector;

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
}
