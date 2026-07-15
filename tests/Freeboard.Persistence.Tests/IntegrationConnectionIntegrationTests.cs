using Dapper;
using Freeboard.Core.GitOps;
using Freeboard.Persistence.GitOps;
using Freeboard.Persistence.System;
using Freeboard.TestInfrastructure;
using MySqlConnector;

namespace Freeboard.Persistence.Tests;

/// <summary>
/// Integration tests for the IntegrationConnection kind and the two integration EvidenceCollector fields
/// against a real MySQL discovered via FREEBOARD_TEST_DB. Each test SKIPS cleanly (not fails) when the
/// env var is absent and gets a fresh throwaway database. Covers migration 018 (table, collector columns,
/// FKs), the connection round-trip through the importer and read model (exposed subset, no token state),
/// the collector's connection_id round-trip, the persisted checks column asserted directly via SQL, and
/// the FK-safe hard-remove ordering (absent collector, then absent connection, then absent vendor).
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class IntegrationConnectionIntegrationTests
{
    private static async Task<MySqlTestDatabase> RequireDbAsync()
    {
        var db = await MySqlTestDatabase.TryCreateAsync();
        Skip.If(db is null, $"{MySqlTestDatabase.EnvVar} not set; skipping MySQL integration test.");
        return db!;
    }

    private static async Task MigrateAsync(MySqlTestDatabase db) =>
        await new MySqlMigrationRunner(db.ConnectionFactory, typeof(IMigrationRunner).Assembly).ApplyPendingAsync();

    private static Standard Std(string id) =>
        new() { Id = id, Title = "T", ApiVersion = "v1", Version = "1.0", Authority = "Example Authority" };

    private static Requirement Req(string id, string standard) =>
        new()
        {
            Id = id,
            Title = "T",
            ApiVersion = "v1",
            Standard = standard,
            Theme = "Theme",
            Statement = "Do the thing.",
            CitationLabel = "Source",
            CitationUrl = "https://example.com/" + id,
        };

    private static Control Ctrl(string id, string[] mapsTo, string evaluation) =>
        new() { Id = id, Title = "T", ApiVersion = "v1", MapsTo = [.. mapsTo], Evaluation = evaluation };

    private static Vendor Vnd(string id) => new() { Id = id, Title = "T", ApiVersion = "v1" };

    private static IntegrationConnection Conn(string id, string vendor = "vendor-a") =>
        new()
        {
            Id = id,
            Title = "Fleet Production",
            ApiVersion = "v1",
            Provider = "fleet",
            BaseUrl = "https://fleet.example.com",
            DiscoveryCadence = "daily",
            Vendor = vendor,
        };

    private static EvidenceCollector IntegrationEc(string id, string connection, List<Check> checks) =>
        new()
        {
            Id = id,
            Title = "T",
            ApiVersion = "v1",
            Control = "ctrl-a",
            Type = "integration",
            Frequency = "daily",
            Connection = connection,
            Checks = checks,
        };

    private static GitOpsConfig Config(
        IEnumerable<IntegrationConnection>? connections = null,
        IEnumerable<EvidenceCollector>? collectors = null,
        IEnumerable<Vendor>? vendors = null) => new()
        {
            Standards = [Std("std-a")],
            Requirements = [Req("req-a", "std-a")],
            Controls = [Ctrl("ctrl-a", ["req-a"], "all")],
            Vendors = vendors?.ToList() ?? [Vnd("vendor-a")],
            IntegrationConnections = connections?.ToList() ?? [],
            EvidenceCollectors = collectors?.ToList() ?? [],
        };

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task Migration018CreatesTableCollectorColumnsAndForeignKeys()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var tables = (await conn.QueryAsync<string>(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = DATABASE();"))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("integration_connections", tables);

        // Binary-collated id and vendor_id on the new table.
        foreach (var column in new[] { "id", "vendor_id" })
        {
            var collation = await conn.ExecuteScalarAsync<string>(
                "SELECT collation_name FROM information_schema.columns "
                + "WHERE table_schema = DATABASE() AND table_name = 'integration_connections' AND column_name = @Column;",
                new { Column = column });
            Assert.Equal("utf8mb4_bin", collation);
        }

        // The connection -> vendors FK is RESTRICT.
        var connectionFkRule = await conn.ExecuteScalarAsync<string>(
            "SELECT delete_rule FROM information_schema.referential_constraints "
            + "WHERE constraint_schema = DATABASE() AND table_name = 'integration_connections';");
        Assert.Equal("RESTRICT", connectionFkRule);

        // evidence_collectors gains connection_id and a JSON checks column.
        var connectionColumn = await conn.ExecuteScalarAsync<string>(
            "SELECT collation_name FROM information_schema.columns "
            + "WHERE table_schema = DATABASE() AND table_name = 'evidence_collectors' AND column_name = 'connection_id';");
        Assert.Equal("utf8mb4_bin", connectionColumn);

        var checksType = await conn.ExecuteScalarAsync<string>(
            "SELECT data_type FROM information_schema.columns "
            + "WHERE table_schema = DATABASE() AND table_name = 'evidence_collectors' AND column_name = 'checks';");
        Assert.Equal("json", checksType);

        // The collector -> integration_connections FK exists and is RESTRICT.
        var collectorConnectionFk = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.referential_constraints "
            + "WHERE constraint_schema = DATABASE() AND table_name = 'evidence_collectors' "
            + "AND referenced_table_name = 'integration_connections' AND delete_rule = 'RESTRICT';");
        Assert.Equal(1, collectorConnectionFk);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task ConnectionAndIntegrationCollectorRoundTrip()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        var importer = new MySqlGitOpsImporter(db.ConnectionFactory);
        var store = new MySqlComplianceStore(db.ConnectionFactory);

        var checks = new List<Check>
        {
            new() { SourceKey = "12", Name = "mfa-enforced", Severity = "Hard" },
            new() { SourceKey = "34", Name = "disk-encrypted", Severity = "Soft" },
        };
        await importer.ImportAsync(Config(
            connections: [Conn("fleet-prod")],
            collectors: [IntegrationEc("collector-a", "fleet-prod", checks)]));

        // The read model exposes only the persisted subset; there is no token state.
        var connection = Assert.Single(await store.GetIntegrationConnectionsAsync());
        Assert.Equal("fleet-prod", connection.Id);
        Assert.Equal("fleet", connection.Provider);
        Assert.Equal("https://fleet.example.com", connection.BaseUrl);
        Assert.Equal("daily", connection.DiscoveryCadence);
        Assert.Equal("vendor-a", connection.Vendor);

        // The collector's connection_id round-trips through the read model.
        var collector = Assert.Single(await store.GetEvidenceCollectorsAsync());
        Assert.Equal("fleet-prod", collector.Connection);

        // Nothing renders checks in V1, so assert the persisted JSON column directly.
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var checksJson = await conn.ExecuteScalarAsync<string>(
            "SELECT checks FROM evidence_collectors WHERE id = 'collector-a';");
        Assert.NotNull(checksJson);

        var sourceKeys = (await conn.QueryAsync<string>(
            "SELECT jt.source_key FROM evidence_collectors ec, "
            + "JSON_TABLE(ec.checks, '$[*]' COLUMNS (source_key VARCHAR(190) PATH '$.SourceKey')) AS jt "
            + "WHERE ec.id = 'collector-a';")).ToList();
        // The stored set equals exactly the authored checks.
        Assert.Equal(["12", "34"], sourceKeys);
        // A Fleet policy id outside the authored list is absent from the column, so it tracks nothing.
        Assert.DoesNotContain("99", sourceKeys);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task HardRemoveDropsCollectorThenConnectionWithoutFkViolation()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        var importer = new MySqlGitOpsImporter(db.ConnectionFactory);
        var store = new MySqlComplianceStore(db.ConnectionFactory);

        var checks = new List<Check> { new() { SourceKey = "12", Name = "mfa-enforced", Severity = "Hard" } };
        // fleet-prod points at vendor-b, and both are removed on re-sync, so the connection-before-vendor
        // prune order is genuinely exercised: a wrong order raises the RESTRICT foreign-key violation.
        await importer.ImportAsync(Config(
            connections: [Conn("fleet-prod", "vendor-b"), Conn("fleet-dev")],
            collectors: [IntegrationEc("collector-a", "fleet-prod", checks)],
            vendors: [Vnd("vendor-a"), Vnd("vendor-b")]));

        // Re-sync dropping the collector and the now-unreferenced fleet-prod connection and vendor-b.
        // The RESTRICT FKs require the collector to be pruned before its connection and the connection
        // before its vendor; a wrong order would raise a foreign-key violation.
        await importer.ImportAsync(Config(
            connections: [Conn("fleet-dev", "vendor-a")],
            collectors: [],
            vendors: [Vnd("vendor-a")]));

        Assert.Empty(await store.GetEvidenceCollectorsAsync());
        var connection = Assert.Single(await store.GetIntegrationConnectionsAsync());
        Assert.Equal("fleet-dev", connection.Id);
        var vendor = Assert.Single(await store.GetVendorsAsync());
        Assert.Equal("vendor-a", vendor.Id);
    }
}
