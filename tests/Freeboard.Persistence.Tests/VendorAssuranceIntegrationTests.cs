using Dapper;
using Freeboard.Core.GitOps;
using Freeboard.Persistence.GitOps;
using Freeboard.Persistence.System;
using Freeboard.TestInfrastructure;
using MySqlConnector;

namespace Freeboard.Persistence.Tests;

/// <summary>
/// Integration tests for the vendor assurance table (migration 023), its whole-set sync replace, and the
/// paired snapshot read, against a real MySQL discovered via FREEBOARD_TEST_DB. Each test SKIPS cleanly
/// when the env var is absent. The removal cases carry the weight here: a stale row left behind by a
/// removed entry misreports a withdrawn certification with no diagnostic anywhere.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class VendorAssuranceIntegrationTests
{
    private static async Task<MySqlTestDatabase> RequireDbAsync()
    {
        var db = await MySqlTestDatabase.TryCreateAsync();
        Skip.If(db is null, $"{MySqlTestDatabase.EnvVar} not set; skipping MySQL integration test.");
        return db!;
    }

    private static Task MigrateAsync(MySqlTestDatabase db) =>
        new MySqlMigrationRunner(db.ConnectionFactory, typeof(IMigrationRunner).Assembly).ApplyPendingAsync();

    private static Asset Org(string id) =>
        new() { Id = id, ApiVersion = "v1", Title = id, Type = "Company", Source = "declared" };

    private static Standard Standard(string id) =>
        new() { Id = id, ApiVersion = "v1", Title = id, Version = "1.0", Authority = "Example Authority" };

    private static Asset Vendor(string id, params Assurance[] assurances) => new()
    {
        Id = id,
        ApiVersion = "v1",
        Title = id,
        Type = "Vendor",
        Source = "declared",
        Owner = "org-a",
        Assurances = [.. assurances],
    };

    private static Assurance Entry(string standard, string expires, string warnDays = "") =>
        new() { Standard = standard, Expires = expires, WarnDays = warnDays };

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task Migration023CreatesTheCompositeKeyBothForeignKeysAndTheCheck()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var key = (await conn.QueryAsync<string>(
            "SELECT column_name FROM information_schema.key_column_usage "
            + "WHERE table_schema = DATABASE() AND table_name = 'vendor_assurances' "
            + "AND constraint_name = 'PRIMARY' ORDER BY ordinal_position;")).ToArray();
        Assert.Equal(["vendor_id", "standard_id"], key);

        var references = (await conn.QueryAsync<(string ConstraintName, string ReferencedTableName)>(
            "SELECT DISTINCT constraint_name AS ConstraintName, referenced_table_name AS ReferencedTableName "
            + "FROM information_schema.key_column_usage WHERE table_schema = DATABASE() "
            + "AND table_name = 'vendor_assurances' AND referenced_table_name IS NOT NULL;")).ToList();
        Assert.Equal("assets", references.Single(r => r.ConstraintName == "fk_vendor_assurances_vendor").ReferencedTableName);
        Assert.Equal("standards", references.Single(r => r.ConstraintName == "fk_vendor_assurances_standard").ReferencedTableName);

        var deleteRules = (await conn.QueryAsync<string>(
            "SELECT delete_rule FROM information_schema.referential_constraints "
            + "WHERE constraint_schema = DATABASE() AND table_name = 'vendor_assurances';")).ToList();
        Assert.All(deleteRules, rule => Assert.Equal("RESTRICT", rule));

        Assert.Equal(1, await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.check_constraints "
            + "WHERE constraint_schema = DATABASE() AND constraint_name = 'ck_vendor_assurances_warn_days';"));

        // The status is derived, never stored.
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() "
            + "AND table_name = 'vendor_assurances' AND column_name = 'status';"));
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task SyncWritesEveryEntryWithAndWithoutAnOverride()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var importer = new MySqlGitOpsImporter(db.ConnectionFactory);

        await importer.ImportAsync(new GitOpsConfig
        {
            Standards = [Standard("std-a"), Standard("std-b")],
            Assets = [Org("org-a"), Vendor("vendor-a", Entry("std-a", "2027-03-27"), Entry("std-b", "2026-11-01", "30"))],
        });

        var inputs = await new MySqlComplianceStore(db.ConnectionFactory).GetVendorAssuranceInputsAsync();

        Assert.Equal(2, inputs.Assurances.Count);
        var soc = inputs.Assurances.Single(a => a.StandardId == "std-a");
        Assert.Equal("vendor-a", soc.VendorId);
        Assert.Equal(new DateOnly(2027, 3, 27), soc.Expires);
        Assert.Null(soc.WarnDays);
        Assert.Equal(30, inputs.Assurances.Single(a => a.StandardId == "std-b").WarnDays);
        Assert.Contains(inputs.Assets, a => a.Id == "vendor-a");
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task DroppingOneEntryDeletesOnlyThatRow()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var importer = new MySqlGitOpsImporter(db.ConnectionFactory);

        await importer.ImportAsync(new GitOpsConfig
        {
            Standards = [Standard("std-a"), Standard("std-b")],
            Assets = [Org("org-a"), Vendor("vendor-a", Entry("std-a", "2027-03-27"), Entry("std-b", "2026-11-01"))],
        });

        await importer.ImportAsync(new GitOpsConfig
        {
            Standards = [Standard("std-a"), Standard("std-b")],
            Assets = [Org("org-a"), Vendor("vendor-a", Entry("std-a", "2027-03-27"))],
        });

        var assurances = (await new MySqlComplianceStore(db.ConnectionFactory).GetVendorAssuranceInputsAsync()).Assurances;

        Assert.Equal("std-a", Assert.Single(assurances).StandardId);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task EmptyingAVendorsListRemovesAllOfItsRows()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var importer = new MySqlGitOpsImporter(db.ConnectionFactory);

        await importer.ImportAsync(new GitOpsConfig
        {
            Standards = [Standard("std-a")],
            Assets = [Org("org-a"), Vendor("vendor-a", Entry("std-a", "2027-03-27"))],
        });

        await importer.ImportAsync(new GitOpsConfig
        {
            Standards = [Standard("std-a")],
            Assets = [Org("org-a"), Vendor("vendor-a")],
        });

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM vendor_assurances;"));
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task DroppingTheVendorRemovesItsRowsAndDoesNotViolateAForeignKey()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var importer = new MySqlGitOpsImporter(db.ConnectionFactory);

        await importer.ImportAsync(new GitOpsConfig
        {
            Standards = [Standard("std-a")],
            Assets = [Org("org-a"), Vendor("vendor-a", Entry("std-a", "2027-03-27"))],
        });

        await importer.ImportAsync(new GitOpsConfig
        {
            Standards = [Standard("std-a")],
            Assets = [Org("org-a")],
        });

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM vendor_assurances;"));
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM assets WHERE id = 'vendor-a';"));
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task DroppingAReferencedStandardWithItsEntrySucceeds()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var importer = new MySqlGitOpsImporter(db.ConnectionFactory);

        await importer.ImportAsync(new GitOpsConfig
        {
            Standards = [Standard("std-a")],
            Assets = [Org("org-a"), Vendor("vendor-a", Entry("std-a", "2027-03-27"))],
        });

        await importer.ImportAsync(new GitOpsConfig { Assets = [Org("org-a"), Vendor("vendor-a")] });

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM vendor_assurances;"));
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM standards;"));
    }
}
