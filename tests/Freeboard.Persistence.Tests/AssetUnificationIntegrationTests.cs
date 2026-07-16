using Dapper;
using Freeboard.Core.Authz;
using Freeboard.Core.GitOps;
using Freeboard.Persistence;
using Freeboard.Persistence.Auth;
using Freeboard.Persistence.GitOps;
using Freeboard.Persistence.System;
using Freeboard.TestInfrastructure;
using MySqlConnector;

namespace Freeboard.Persistence.Tests;

/// <summary>
/// Integration tests for the asset unification (migration 019, the merged schema, and the declared-only
/// sync) against a real MySQL discovered via FREEBOARD_TEST_DB. Each test SKIPS cleanly when the env var
/// is absent.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class AssetUnificationIntegrationTests
{
    private static async Task<MySqlTestDatabase> RequireDbAsync()
    {
        var db = await MySqlTestDatabase.TryCreateAsync();
        Skip.If(db is null, $"{MySqlTestDatabase.EnvVar} not set; skipping MySQL integration test.");
        return db!;
    }

    private static Task MigrateAsync(MySqlTestDatabase db) =>
        new MySqlMigrationRunner(db.ConnectionFactory, typeof(IMigrationRunner).Assembly).ApplyPendingAsync();

    private static Asset DeclaredOrg(string id, string type = "Company", string? parent = null) =>
        new() { Id = id, ApiVersion = "v1", Title = id, Type = type, Source = "declared", Parent = parent ?? string.Empty };

    private static Asset DeclaredVendor(string id, string? owner = null) =>
        new() { Id = id, ApiVersion = "v1", Title = id, Type = "Vendor", Source = "declared", Owner = owner ?? string.Empty };

    private static NewMachineObservation Obs(string org, string source, string externalId, string serial) =>
        new(org, source, externalId, serial, null, null);

    // Migration 019 re-points every downstream FK at assets and drops the old tables.
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task Migration019RepointsAllForeignKeysAndDropsOldTables()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var tables = (await conn.QueryAsync<string>(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = DATABASE();"))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("assets", tables);
        Assert.Contains("asset_source", tables);
        Assert.DoesNotContain("organisations", tables);
        Assert.DoesNotContain("vendors", tables);
        Assert.DoesNotContain("asset", tables);

        // Every retargeted FK now references assets, and none references a dropped table.
        var repointed = new[]
        {
            "fk_scopes_organisation", "fk_requirement_scopes_organisation", "fk_authz_org_role_assignments_org",
            "fk_vendor_scopes_vendor", "fk_evidence_collectors_vendor", "fk_integration_connections_vendor",
            "fk_asset_source_asset",
        };
        foreach (var fk in repointed)
        {
            var referenced = await conn.ExecuteScalarAsync<string?>(
                "SELECT DISTINCT referenced_table_name FROM information_schema.key_column_usage "
                + "WHERE table_schema = DATABASE() AND constraint_name = @Fk AND referenced_table_name IS NOT NULL;",
                new { Fk = fk });
            Assert.Equal("assets", referenced);
        }

        var danglingFks = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.key_column_usage "
            + "WHERE table_schema = DATABASE() AND referenced_table_name IN ('organisations', 'vendors', 'asset');");
        Assert.Equal(0, danglingFks);
    }

    // The copy is deterministic under disjoint ids and fails loudly on a duplicate primary key.
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task Migration019CopiesDisjointIdsAndFailsOnCollision()
    {
        await using var db = await RequireDbAsync();
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var migrations = MigrationCatalog.Load(typeof(IMigrationRunner).Assembly);
        var pre019 = migrations.Where(m => m.Ordinal < 19).ToList();
        var m019 = migrations.Single(m => m.Ordinal == 19);
        foreach (var migration in pre019)
        {
            await conn.ExecuteAsync(migration.Sql);
        }

        // Disjoint org and vendor ids copy into assets without collision.
        await conn.ExecuteAsync(
            "INSERT INTO organisations (id, api_version, title, kind, created_at, updated_at) "
            + "VALUES ('org-disjoint', 'v1', 'Org', 'Company', NOW(6), NOW(6));");
        await conn.ExecuteAsync(
            "INSERT INTO vendors (id, api_version, title, created_at, updated_at) "
            + "VALUES ('vendor-disjoint', 'v1', 'Vendor', NOW(6), NOW(6));");
        await conn.ExecuteAsync(m019.Sql);

        Assert.Equal(1, await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM assets WHERE id = 'org-disjoint' AND type = 'Company' AND source = 'declared';"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM assets WHERE id = 'vendor-disjoint' AND type = 'Vendor';"));

        // A fresh database seeded with an org and a vendor sharing an id must fail 019 on the duplicate key.
        await using var db2 = await RequireDbAsync();
        await using var conn2 = new MySqlConnection(db2.ConnectionString);
        await conn2.OpenAsync();
        foreach (var migration in pre019)
        {
            await conn2.ExecuteAsync(migration.Sql);
        }

        await conn2.ExecuteAsync(
            "INSERT INTO organisations (id, api_version, title, kind, created_at, updated_at) "
            + "VALUES ('shared-id', 'v1', 'Org', 'Company', NOW(6), NOW(6));");
        await conn2.ExecuteAsync(
            "INSERT INTO vendors (id, api_version, title, created_at, updated_at) "
            + "VALUES ('shared-id', 'v1', 'Vendor', NOW(6), NOW(6));");

        var ex = await Assert.ThrowsAsync<MySqlException>(() => conn2.ExecuteAsync(m019.Sql));
        Assert.Equal(MySqlErrorCode.DuplicateKeyEntry, ex.ErrorCode);
    }

    // A declared-only sync upserts declared assets, hard-removes an absent one, and never touches
    // a discovered machine; an empty-config sync likewise leaves the discovered inventory intact.
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task DeclaredOnlySyncReconcilesDeclaredAndLeavesDiscoveredUntouched()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var importer = new MySqlGitOpsImporter(db.ConnectionFactory);
        var writes = new MySqlAssetWriteStore(db.ConnectionFactory, new UlidFactory());
        var reads = new MySqlAssetStore(db.ConnectionFactory);

        // A discovered machine under org-a.
        var machine = await writes.UpsertMachineFromSourceAsync(Obs("org-a", "fleetdm", "host-1", "SN-1"));
        Assert.Equal(AssetUpsertStatus.Created, machine.Status);

        // First sync declares two orgs.
        await importer.ImportAsync(new GitOpsConfig { Assets = [DeclaredOrg("org-a"), DeclaredOrg("org-b")] });
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        Assert.Equal(2, await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM assets WHERE source = 'declared';"));

        // Second sync drops org-b; it is hard-removed, the discovered machine survives.
        await importer.ImportAsync(new GitOpsConfig { Assets = [DeclaredOrg("org-a")] });
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM assets WHERE id = 'org-b';"));
        Assert.NotNull(await reads.GetByIdAsync("org-a", machine.AssetId!));

        // An empty-config sync removes every declared asset but never the discovered machine.
        await importer.ImportAsync(new GitOpsConfig());
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM assets WHERE source = 'declared';"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM assets WHERE source = 'discovered';"));
        Assert.NotNull(await reads.GetByIdAsync("org-a", machine.AssetId!));
    }

    // A declared id colliding with an existing discovered ULID fails the sync before any write.
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task DeclaredIdCollidingWithDiscoveredFailsSyncWithNoMutation()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var importer = new MySqlGitOpsImporter(db.ConnectionFactory);
        var writes = new MySqlAssetWriteStore(db.ConnectionFactory, new UlidFactory());

        var machine = await writes.UpsertMachineFromSourceAsync(Obs("org-a", "fleetdm", "host-1", "SN-1"));
        var discoveredId = machine.AssetId!;

        // A prior declared org so the sync would otherwise have something to write.
        await importer.ImportAsync(new GitOpsConfig { Assets = [DeclaredOrg("org-a")] });

        var colliding = new GitOpsConfig { Assets = [DeclaredOrg("org-a"), DeclaredOrg(discoveredId)] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportAsync(colliding));

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        // Nothing mutated: the discovered row still has source 'discovered', and org-a still present.
        Assert.Equal("discovered", await conn.ExecuteScalarAsync<string>(
            "SELECT source FROM assets WHERE id = @Id;", new { Id = discoveredId }));
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM assets WHERE id = 'org-a';"));
    }

    // A dangling parent/owner is a warning, never a sync failure; a discovered machine whose
    // declared parent was removed survives with a dangling parent.
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task DanglingEdgesDoNotFailSyncAndDiscoveredChildSurvives()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var importer = new MySqlGitOpsImporter(db.ConnectionFactory);
        var writes = new MySqlAssetWriteStore(db.ConnectionFactory, new UlidFactory());

        await writes.UpsertMachineFromSourceAsync(Obs("org-a", "fleetdm", "host-1", "SN-1"));

        // org-a is declared, then removed; a child asset naming the absent parent still syncs.
        await importer.ImportAsync(new GitOpsConfig { Assets = [DeclaredOrg("org-a")] });
        await importer.ImportAsync(new GitOpsConfig
        {
            Assets = [DeclaredOrg("dept-x", "Department", parent: "org-a"), DeclaredVendor("vendor-x", owner: "org-a")],
        });

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        Assert.Equal("org-a", await conn.ExecuteScalarAsync<string>(
            "SELECT parent FROM assets WHERE id = 'dept-x';"));
        // The discovered machine's parent org-a is gone, but the machine row remains.
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM assets WHERE source = 'discovered' AND parent = 'org-a';"));
    }

    // The source-filtered read keeps its org predicate against a.parent, so a cross-org
    // asset_source row cannot surface another org's machine; discovered dedup still resolves to one row.
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task SourceReadIsOrgScopedAndDiscoveredDedupHolds()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var writes = new MySqlAssetWriteStore(db.ConnectionFactory, new UlidFactory());
        var reads = new MySqlAssetStore(db.ConnectionFactory);

        var a = await writes.UpsertMachineFromSourceAsync(Obs("org-a", "fleetdm", "host-1", "SN-1"));

        // A hand-written asset_source row under org-b pointing at org-a's machine must NOT surface it: the
        // read predicate s.organisation_id = a.parent filters it out (query-enforced isolation).
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            "INSERT INTO asset_source (id, asset_id, organisation_id, source, external_id, "
            + "observed_serial, observed_host_uuid, first_seen_at, last_seen_at, created_at) "
            + "VALUES ('00000000000000000000000009', @AssetId, 'org-b', 'fleetdm', 'x', NULL, NULL, "
            + "NOW(6), NOW(6), NOW(6));",
            new { a.AssetId });
        Assert.Null(await reads.GetBySourceAsync("org-b", "fleetdm", "x"));

        // Two sources reporting the same serial under org-a dedup to one machine via
        // (parent, identity_kind, identity_value).
        var second = await writes.UpsertMachineFromSourceAsync(Obs("org-a", "mdm", "device-7", "SN-1"));
        Assert.Equal(a.AssetId, second.AssetId);
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM assets WHERE source = 'discovered' AND parent = 'org-a' "
            + "AND identity_kind = 'Serial' AND identity_value = 'SN-1';"));
    }

    // The app-managed org CRUD writes to the merged assets table and keeps its stricter
    // write-time guards (self-parent, cycle, no-delete-with-children); org-role assignment validates the
    // target against assets.
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task OrgCrudAndAuthzAssignmentWorkAgainstMergedAssets()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var store = new MySqlComplianceWriteStore(db.ConnectionFactory);
        var reads = new MySqlComplianceStore(db.ConnectionFactory);

        Assert.True((await store.UpsertOrganisationAsync("root", "Root", "Company", null)).Ok);
        Assert.True((await store.UpsertOrganisationAsync("child", "Child", "Department", "root", expectExisting: false)).Ok);

        // The org projection reads back the two Company/Department assets.
        var orgs = await reads.GetOrganisationsAsync();
        Assert.Equal(["child", "root"], orgs.Select(o => o.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Equal("root", orgs.Single(o => o.Id == "child").Parent);

        // Self-parent and cycle are rejected as app-level hard errors (stricter than sync tolerance).
        Assert.False((await store.UpsertOrganisationAsync("root", "Root", "Company", "root", expectExisting: true, expectedCurrentParent: null)).Ok);
        Assert.False((await store.UpsertOrganisationAsync("root", "Root", "Company", "child", expectExisting: true, expectedCurrentParent: null)).Ok);

        // Cannot delete an org that still has children.
        Assert.False((await store.DeleteOrganisationAsync("root")).Ok);
        Assert.True((await store.DeleteOrganisationAsync("child")).Ok);
        Assert.True((await store.DeleteOrganisationAsync("root")).Ok);
        Assert.Empty(await reads.GetOrganisationsAsync());

        // Authz org-role assignment validates the target org against the Company/Department asset subset.
        var authz = new MySqlAuthzAdministrationStore(db.ConnectionFactory, new UlidFactory());
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            "INSERT INTO users (id, email, email_normalized, name, global_role, enabled, force_password_reset, mfa_enabled, created_at, updated_at) "
            + "VALUES ('user-1', 'u@example.com', 'u@example.com', 'U', 'member', 1, 0, 0, NOW(6), NOW(6));");

        // No such org -> Invalid.
        var missing = await authz.AssignOrganisationRoleAsync("user-1", AuthzRoles.ComplianceReader, "no-such-org");
        Assert.Equal(AuthzWriteStatus.Invalid, missing.Status);

        // A real Company asset -> assignment succeeds.
        Assert.True((await store.UpsertOrganisationAsync("org-x", "Org X", "Company", null)).Ok);
        var granted = await authz.AssignOrganisationRoleAsync("user-1", AuthzRoles.ComplianceReader, "org-x");
        Assert.Equal(AuthzWriteStatus.Ok, granted.Status);
    }
}
