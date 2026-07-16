using Dapper;
using Freeboard.Core.Authz;
using Freeboard.Persistence;
using Freeboard.Persistence.Auth;
using Freeboard.Persistence.GitOps;
using Freeboard.Persistence.System;
using Freeboard.TestInfrastructure;
using MySqlConnector;

namespace Freeboard.Persistence.Tests;

/// <summary>
/// Integration tests for the authz tables, seed, backfills, and store pair against a real MySQL
/// discovered via FREEBOARD_TEST_DB. Each test SKIPS cleanly when the env var is absent.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class AuthzIntegrationTests
{
    private static async Task<MySqlTestDatabase> RequireDbAsync()
    {
        var db = await MySqlTestDatabase.TryCreateAsync();
        Skip.If(db is null, $"{MySqlTestDatabase.EnvVar} not set; skipping MySQL integration test.");
        return db!;
    }

    private static async Task MigrateAsync(MySqlTestDatabase db) =>
        await new MySqlMigrationRunner(db.ConnectionFactory, typeof(IMigrationRunner).Assembly).ApplyPendingAsync();

    // Apply every migration before 019 on an open connection. Migration 010's backfills read the
    // organisations table, which 019 folds into assets and drops, so a test that replays 010 must run
    // against the pre-019 schema it was authored for.
    private static async Task ApplyPre019Async(MySqlConnection conn)
    {
        foreach (var migration in MigrationCatalog.Load(typeof(IMigrationRunner).Assembly).Where(m => m.Ordinal < 19))
        {
            await conn.ExecuteAsync(migration.Sql);
        }
    }

    private static Task InsertOrganisationRowAsync(MySqlConnection conn, string id, string? parent = null)
        => conn.ExecuteAsync(
            "INSERT INTO organisations (id, api_version, title, kind, parent_id, created_at, updated_at) "
            + "VALUES (@Id, 'v1', @Id, 'Company', @Parent, NOW(6), NOW(6));",
            new { Id = id, Parent = parent });

    private static async Task InsertUserAsync(
        MySqlConnection conn, string id, string role, bool enabled = true, bool withCredential = true)
    {
        await conn.ExecuteAsync(
            "INSERT INTO users (id, email, email_normalized, name, global_role, enabled, force_password_reset, mfa_enabled, created_at, updated_at) "
            + "VALUES (@Id, @Email, @Email, @Id, @Role, @Enabled, 0, 0, NOW(6), NOW(6));",
            new { Id = id, Email = $"{id}@example.com", Role = role, Enabled = enabled ? 1 : 0 });
        if (withCredential)
        {
            await conn.ExecuteAsync(
                "INSERT INTO user_password_credentials (user_id, password_hash, secret_version, updated_at) "
                + "VALUES (@Id, 'hash', 1, NOW(6));",
                new { Id = id });
        }
    }

    private static async Task InsertOrgAsync(MySqlConnection conn, string id, string? parent = null)
        => await conn.ExecuteAsync(
            "INSERT INTO assets (id, type, source, api_version, title, parent, created_at, updated_at) "
            + "VALUES (@Id, 'Company', 'declared', 'v1', @Id, @Parent, NOW(6), NOW(6));",
            new { Id = id, Parent = parent });

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task MigrationSeedsRolesWithExpectedScope()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var scopes = (await conn.QueryAsync<(string RoleKey, string Scope)>(
            "SELECT role_key AS RoleKey, scope AS Scope FROM authz_roles;"))
            .ToDictionary(r => r.RoleKey, r => r.Scope, StringComparer.Ordinal);

        Assert.Equal("system", scopes["super-admin"]);
        Assert.Equal("organisation", scopes["org-owner"]);
        Assert.Equal("organisation", scopes["compliance-manager"]);
        Assert.Equal("organisation", scopes["compliance-reader"]);

        var perms = (await conn.QueryAsync<string>("SELECT permission_key FROM authz_permissions;"))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(8, perms.Count);
        Assert.Contains("system.admin", perms);

        // user.manage is held by no seeded role.
        var userManageRoles = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM authz_role_permissions WHERE permission_key = 'user.manage';");
        Assert.Equal(0, userManageRoles);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task AdminAndMemberBackfillsRunOnMigration()
    {
        await using var db = await RequireDbAsync();
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        // Apply the pre-019 schema (organisations still present), then seed users and root/child orgs.
        await ApplyPre019Async(conn);
        await InsertUserAsync(conn, "admin1", "admin");
        await InsertUserAsync(conn, "member1", "member");
        await InsertUserAsync(conn, "disabled1", "member", enabled: false);
        await InsertOrganisationRowAsync(conn, "root1");
        await InsertOrganisationRowAsync(conn, "child1", "root1");

        // Re-run the migration file so its backfills apply to the now-seeded rows (idempotent).
        var raw = await conn.ReadMigration010Async();
        await conn.ExecuteAsync(raw);

        var superAdmins = (await conn.QueryAsync<string>(
            "SELECT user_id FROM authz_system_role_assignments WHERE role_key = 'super-admin';"))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("admin1", superAdmins);

        var readerGrants = (await conn.QueryAsync<(string UserId, string OrgId)>(
            "SELECT user_id AS UserId, organisation_id AS OrgId FROM authz_organisation_role_assignments WHERE role_key = 'compliance-reader';"))
            .ToList();
        // Enabled non-admin gets compliance-reader on the ROOT org only (a root grant covers the tree).
        Assert.Contains(("member1", "root1"), readerGrants);
        Assert.DoesNotContain(("member1", "child1"), readerGrants);
        // Disabled member is not backfilled; admin is not backfilled as a reader.
        Assert.DoesNotContain(readerGrants, g => g.UserId == "disabled1");
        Assert.DoesNotContain(readerGrants, g => g.UserId == "admin1");
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task MigrationReRunIsIdempotent()
    {
        await using var db = await RequireDbAsync();
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        // Pre-019 runs 010 once; replaying its file must not throw or duplicate seed rows.
        await ApplyPre019Async(conn);
        var raw = await conn.ReadMigration010Async();
        await conn.ExecuteAsync(raw);

        var roleCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM authz_roles;");
        Assert.Equal(4, roleCount);
        var permCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM authz_permissions;");
        Assert.Equal(8, permCount);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task AdminCreateWritesUserCredentialAndAssignmentAtomically()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        var users = new MySqlUserStore(db.ConnectionFactory, new UlidFactory());
        var created = await users.CreateAdminAsync(new NewUser("a@example.com", "A", "admin"), "hash", 1, true);

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM user_password_credentials WHERE user_id = @Id;", new { created.Id }));
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM authz_system_role_assignments WHERE user_id = @Id AND role_key = 'super-admin';",
            new { created.Id }));
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task AdminCreateRollsBackWholeCreateOnFailureLeavingNoOrphanSuperAdmin()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        var users = new MySqlUserStore(db.ConnectionFactory, new UlidFactory());
        await users.CreateAdminAsync(new NewUser("dup@example.com", "A", "admin"), "hash", 1, true);

        var before = await SuperAdminCountAsync(db);
        // A duplicate email fails the create; the whole transaction rolls back, so no new user,
        // credential, or super-admin assignment is left behind.
        await Assert.ThrowsAsync<MySqlException>(() =>
            users.CreateAdminAsync(new NewUser("dup@example.com", "B", "admin"), "hash", 1, true));
        Assert.Equal(before, await SuperAdminCountAsync(db));
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task AdminCreateRollsBackWhenCredentialFaultsAfterUsersInsert()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        var users = new MySqlUserStore(db.ConnectionFactory, new UlidFactory());
        var before = await SuperAdminCountAsync(db);

        // A fault AFTER the users row inserts: an over-long hash overflows password_hash VARCHAR(255),
        // so the credential insert fails once the users row is already staged. This exercises the
        // transaction ordering (not the first-insert duplicate-email path): the whole create must roll
        // back, leaving NO users row and NO orphan super-admin assignment.
        var overlong = new string('x', 300);
        await Assert.ThrowsAsync<MySqlException>(() =>
            users.CreateAdminAsync(new NewUser("fault@example.com", "F", "admin"), overlong, 1, true));

        Assert.Equal(before, await SuperAdminCountAsync(db));
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM users WHERE email_normalized = 'fault@example.com';"));
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task ScopeUpsertRejectsWhenLockedCurrentOwnerDiffersFromAuthorized()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await InsertOrgAsync(conn, "org-a");
        await InsertOrgAsync(conn, "org-b");
        await conn.ExecuteAsync(
            "INSERT INTO standards (id, api_version, title, created_at, updated_at) VALUES ('std-a', 'v1', 'S', NOW(6), NOW(6));");

        var writes = new MySqlComplianceWriteStore(db.ConnectionFactory);
        Assert.True((await writes.UpsertScopeDispositionAsync("s1", "T", "org-a", "std-a", "In")).Ok);

        // The caller authorized "org-b" as the current owner, but the row is actually owned by org-a
        // (a concurrent move would produce this mismatch). The FOR UPDATE re-check rejects it as a
        // conflict rather than blindly overwriting a row whose owner changed under authorization.
        var stale = await writes.UpsertScopeDispositionAsync("s1", "T2", "org-a", "std-a", "Out", expectedCurrentOrganisation: "org-b");
        Assert.True(stale.IsConflict);

        // The matching authorized owner passes: the current owner is unchanged under the lock.
        var ok = await writes.UpsertScopeDispositionAsync("s1", "T2", "org-a", "std-a", "Out", expectedCurrentOrganisation: "org-a");
        Assert.True(ok.Ok, ok.Error);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task OrgReparentRejectsWhenLockedCurrentParentDiffersFromAuthorized()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await InsertOrgAsync(conn, "parent-a");
        await InsertOrgAsync(conn, "parent-b");
        await InsertOrgAsync(conn, "child", "parent-a");

        var writes = new MySqlComplianceWriteStore(db.ConnectionFactory);

        // The caller authorized "parent-b" as the current parent, but the row is actually under parent-a
        // (a concurrent reparent would produce this mismatch). The FOR UPDATE re-check rejects it as a
        // conflict rather than moving a row whose current parent changed under authorization.
        var stale = await writes.UpsertOrganisationAsync(
            "child", "Child", "Company", "parent-b", expectExisting: true, expectedCurrentParent: "parent-b");
        Assert.True(stale.IsConflict);

        // The matching authorized parent passes: the current parent is unchanged under the lock.
        var ok = await writes.UpsertOrganisationAsync(
            "child", "Child", "Company", "parent-b", expectExisting: true, expectedCurrentParent: "parent-a");
        Assert.True(ok.Ok, ok.Error);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task OrgDeletePrunesAssignmentsAppPath()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await InsertUserAsync(conn, "u1", "member");
        await InsertOrgAsync(conn, "org1");

        var admin = new MySqlAuthzAdministrationStore(db.ConnectionFactory, new UlidFactory());
        Assert.True((await admin.AssignOrganisationRoleAsync("u1", "org-owner", "org1")).IsOk);

        var writes = new MySqlComplianceWriteStore(db.ConnectionFactory);
        var result = await writes.DeleteOrganisationAsync("org1");
        Assert.True(result.Ok, result.Error);

        Assert.Equal(0, await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM authz_organisation_role_assignments WHERE organisation_id = 'org1';"));
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task ImporterPrunesAssignmentsForAbsentOrganisations()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await InsertUserAsync(conn, "u1", "member");
        await InsertOrgAsync(conn, "gone");

        var admin = new MySqlAuthzAdministrationStore(db.ConnectionFactory, new UlidFactory());
        Assert.True((await admin.AssignOrganisationRoleAsync("u1", "compliance-reader", "gone")).IsOk);

        // An import whose config omits 'gone' prunes its assignment then deletes the org (RESTRICT-safe).
        var importer = new MySqlGitOpsImporter(db.ConnectionFactory);
        await importer.ImportAsync(new Core.GitOps.GitOpsConfig
        {
            Standards = [],
            Requirements = [],
            Controls = [],
            Assets = [],
            Scopes = [],
            RequirementScopes = [],
        });

        Assert.Equal(0, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM assets WHERE id = 'gone';"));
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM authz_organisation_role_assignments WHERE organisation_id = 'gone';"));
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task AssignRevokeRoundTripAndDuplicateRejected()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await InsertUserAsync(conn, "u1", "member");
        await InsertOrgAsync(conn, "org1");

        var admin = new MySqlAuthzAdministrationStore(db.ConnectionFactory, new UlidFactory());
        Assert.True((await admin.AssignOrganisationRoleAsync("u1", "compliance-manager", "org1")).IsOk);
        Assert.Equal(AuthzWriteStatus.Conflict,
            (await admin.AssignOrganisationRoleAsync("u1", "compliance-manager", "org1")).Status);
        Assert.True((await admin.RevokeOrganisationRoleAsync("u1", "compliance-manager", "org1", "actor")).IsOk);
        Assert.Equal(AuthzWriteStatus.NotFound,
            (await admin.RevokeOrganisationRoleAsync("u1", "compliance-manager", "org1", "actor")).Status);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task MisScopedGrantRejectedAndIgnoredByFactLoader()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await InsertUserAsync(conn, "u1", "member");
        await InsertOrgAsync(conn, "org1");

        var admin = new MySqlAuthzAdministrationStore(db.ConnectionFactory, new UlidFactory());
        // super-admin is a system role; assigning it through the ORG write is rejected and writes nothing.
        Assert.Equal(AuthzWriteStatus.Invalid,
            (await admin.AssignOrganisationRoleAsync("u1", "super-admin", "org1")).Status);
        // An org role through the SYSTEM write is rejected too.
        Assert.Equal(AuthzWriteStatus.Invalid,
            (await admin.AssignSystemRoleAsync("u1", "compliance-reader")).Status);

        // Force a stray mis-scoped row directly, then prove the fact loader ignores it (no permit-all).
        await conn.ExecuteAsync(
            "INSERT INTO authz_organisation_role_assignments (user_id, role_key, organisation_id, created_at, updated_at) "
            + "VALUES ('u1', 'super-admin', 'org1', NOW(6), NOW(6));");
        var facts = await new MySqlAuthzStore(db.ConnectionFactory).LoadPrincipalFactsAsync("u1");
        Assert.DoesNotContain(AuthzActions.SystemAdmin, facts.SystemPermissions);
        Assert.DoesNotContain(facts.OrgGrants, g => g.PermissionKey == AuthzActions.SystemAdmin);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task LastSuperAdminRevokeRejectedButCredentiallessDoesNotCount()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await InsertUserAsync(conn, "sa1", "admin");
        await InsertUserAsync(conn, "sa2", "admin", withCredential: false); // credential-less: not usable

        var admin = new MySqlAuthzAdministrationStore(db.ConnectionFactory, new UlidFactory());
        Assert.True((await admin.AssignSystemRoleAsync("sa1", "super-admin")).IsOk);
        Assert.True((await admin.AssignSystemRoleAsync("sa2", "super-admin")).IsOk);

        // sa2 has no credential, so sa1 is the ONLY usable super-admin; revoking sa1 is rejected.
        Assert.Equal(AuthzWriteStatus.Conflict, (await admin.RevokeSystemRoleAsync("sa1", "super-admin")).Status);
        // Revoking the credential-less sa2 is allowed (it never counted as usable).
        Assert.True((await admin.RevokeSystemRoleAsync("sa2", "super-admin")).IsOk);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task LastDirectOrgOwnerRevokeRejected()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await InsertUserAsync(conn, "u1", "member");
        await InsertOrgAsync(conn, "org1");

        var admin = new MySqlAuthzAdministrationStore(db.ConnectionFactory, new UlidFactory());
        Assert.True((await admin.AssignOrganisationRoleAsync("u1", "org-owner", "org1")).IsOk);
        Assert.Equal(AuthzWriteStatus.Conflict, (await admin.RevokeOrganisationRoleAsync("u1", "org-owner", "org1", "actor")).Status);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task OrgOwnerCannotRevokeItsOwnGrantSelfLockout()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await InsertUserAsync(conn, "owner1", "member");
        await InsertUserAsync(conn, "owner2", "member");
        await InsertOrgAsync(conn, "org1");

        var admin = new MySqlAuthzAdministrationStore(db.ConnectionFactory, new UlidFactory());
        Assert.True((await admin.AssignOrganisationRoleAsync("owner1", "org-owner", "org1")).IsOk);
        Assert.True((await admin.AssignOrganisationRoleAsync("owner2", "org-owner", "org1")).IsOk);

        // Two owners remain, so the last-owner guard does not fire; the self-lockout guard rejects a
        // caller revoking its OWN org-owner grant, but permits it to revoke another owner's grant.
        Assert.Equal(AuthzWriteStatus.Conflict,
            (await admin.RevokeOrganisationRoleAsync("owner1", "org-owner", "org1", "owner1")).Status);
        Assert.True((await admin.RevokeOrganisationRoleAsync("owner2", "org-owner", "org1", "owner1")).IsOk);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task TwoParallelRevokesOfLastTwoUsableSuperAdminsCannotBothSucceed()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await InsertUserAsync(conn, "sa1", "admin");
        await InsertUserAsync(conn, "sa2", "admin");

        var admin = new MySqlAuthzAdministrationStore(db.ConnectionFactory, new UlidFactory());
        Assert.True((await admin.AssignSystemRoleAsync("sa1", "super-admin")).IsOk);
        Assert.True((await admin.AssignSystemRoleAsync("sa2", "super-admin")).IsOk);

        var t1 = admin.RevokeSystemRoleAsync("sa1", "super-admin");
        var t2 = admin.RevokeSystemRoleAsync("sa2", "super-admin");
        var results = await Task.WhenAll(t1, t2);

        Assert.Equal(1, results.Count(r => r.IsOk));
        Assert.Equal(1, results.Count(r => r.Status == AuthzWriteStatus.Conflict));
        Assert.True(await SuperAdminCountAsync(db) >= 1);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task PersistedMutationWritesAuditRowAndFactLoadReturnsPermissions()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await InsertUserAsync(conn, "u1", "member");
        await InsertOrgAsync(conn, "org1");

        var admin = new MySqlAuthzAdministrationStore(db.ConnectionFactory, new UlidFactory());
        await admin.AssignOrganisationRoleAsync("u1", "org-owner", "org1");
        await admin.AppendAuditEventAsync(new AuthzAuditEvent(
            "authz.assignment.write", "actor1", "authz.assignment.write", "user", "u1", "org1", "Permit", "granted"));

        Assert.Equal(1, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM authz_audit_events;"));

        var facts = await new MySqlAuthzStore(db.ConnectionFactory).LoadPrincipalFactsAsync("u1");
        Assert.Contains(facts.OrgGrants, g => g.PermissionKey == AuthzActions.OrgWrite && g.OrganisationId == "org1");
        Assert.Contains(facts.OrgGrants, g => g.PermissionKey == AuthzActions.AuthzAssignmentWrite && g.OrganisationId == "org1");
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task UserDeleteCascadesAssignments()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await InsertUserAsync(conn, "u1", "member");
        await InsertOrgAsync(conn, "org1");

        var admin = new MySqlAuthzAdministrationStore(db.ConnectionFactory, new UlidFactory());
        await admin.AssignOrganisationRoleAsync("u1", "compliance-reader", "org1");
        await conn.ExecuteAsync("DELETE FROM users WHERE id = 'u1';");
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM authz_organisation_role_assignments WHERE user_id = 'u1';"));
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task CreateCustomRoleWritesScopePermissionsAndAuditAtomically()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var admin = new MySqlAuthzAdministrationStore(db.ConnectionFactory, new UlidFactory());
        var result = await admin.CreateCustomRoleAsync(
            "custom:auditor", "Auditor", "Read-only auditor.",
            [AuthzActions.OrgRead, AuthzActions.ComplianceRead], "actor1");
        Assert.True(result.IsOk, result.Error);

        var row = (await conn.QueryAsync<(string Scope, bool IsSystem)>(
            "SELECT scope AS Scope, is_system AS IsSystem FROM authz_roles WHERE role_key = 'custom:auditor';")).Single();
        Assert.Equal("organisation", row.Scope);
        Assert.False(row.IsSystem);

        var perms = (await conn.QueryAsync<string>(
            "SELECT permission_key FROM authz_role_permissions WHERE role_key = 'custom:auditor' ORDER BY permission_key;"))
            .ToList();
        Assert.Equal([AuthzActions.ComplianceRead, AuthzActions.OrgRead], perms);

        Assert.Equal(1, await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM authz_audit_events WHERE event_type = 'authz.role.create' AND resource_id = 'custom:auditor';"));
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task AuditInsertFailureRollsBackTheWholeMutation()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        // Force the audit insert (the last write in the transaction) to collide on the audit PK by
        // pre-seeding a row with the id the store will generate. If the mutation were audited
        // best-effort after commit, the role would survive; because the audit write is inside the
        // transaction, the collision rolls the role and its permission rows back too.
        const string collidingId = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
        await conn.ExecuteAsync(
            "INSERT INTO authz_audit_events (id, occurred_at, event_type) VALUES (@Id, UTC_TIMESTAMP(6), 'seed');",
            new { Id = collidingId });

        var admin = new MySqlAuthzAdministrationStore(db.ConnectionFactory, new FixedUlidFactory(collidingId));
        var result = await admin.CreateCustomRoleAsync(
            "custom:atomic", "Atomic", "desc", [AuthzActions.OrgRead], "actor1");

        Assert.False(result.IsOk);
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM authz_roles WHERE role_key = 'custom:atomic';"));
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM authz_role_permissions WHERE role_key = 'custom:atomic';"));
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM authz_audit_events WHERE resource_id = 'custom:atomic';"));
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task DuplicatePermissionKeysAreCollapsedNotConflict()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var admin = new MySqlAuthzAdministrationStore(db.ConnectionFactory, new UlidFactory());
        var result = await admin.CreateCustomRoleAsync(
            "custom:dupperm", "Dup", "", [AuthzActions.OrgRead, AuthzActions.OrgRead, AuthzActions.ComplianceRead], "actor1");
        Assert.True(result.IsOk, result.Error);

        var perms = (await conn.QueryAsync<string>(
            "SELECT permission_key FROM authz_role_permissions WHERE role_key = 'custom:dupperm' ORDER BY permission_key;"))
            .ToList();
        Assert.Equal([AuthzActions.ComplianceRead, AuthzActions.OrgRead], perms);

        // The replace-on-update path also tolerates a duplicate submitted set.
        Assert.True((await admin.UpdateCustomRoleAsync(
            "custom:dupperm", "Dup", "", [AuthzActions.OrgWrite, AuthzActions.OrgWrite], "actor1")).IsOk);
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM authz_role_permissions WHERE role_key = 'custom:dupperm';"));
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task NullDescriptionIsStoredAsEmptyString()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var admin = new MySqlAuthzAdministrationStore(db.ConnectionFactory, new UlidFactory());
        Assert.True((await admin.CreateCustomRoleAsync(
            "custom:nodesc", "No Desc", null!, [AuthzActions.OrgRead], "actor1")).IsOk);

        var description = await conn.ExecuteScalarAsync<string>(
            "SELECT description FROM authz_roles WHERE role_key = 'custom:nodesc';");
        Assert.Equal(string.Empty, description);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task CreateCustomRoleRoundTripsThroughGetRole()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        var admin = new MySqlAuthzAdministrationStore(db.ConnectionFactory, new UlidFactory());
        await admin.CreateCustomRoleAsync(
            "custom:tier-2", "Tier 2", "Scope writer.", [AuthzActions.ComplianceScopeWrite], "actor1");

        var store = new MySqlAuthzStore(db.ConnectionFactory);
        var loaded = await store.GetRoleAsync("custom:tier-2");
        Assert.NotNull(loaded);
        Assert.Equal("Tier 2", loaded!.Role.Title);
        Assert.False(loaded.Role.IsSystem);
        Assert.Equal([AuthzActions.ComplianceScopeWrite], loaded.PermissionKeys);

        var listed = await store.ListCustomRolesAsync();
        Assert.Contains(listed, r => r.RoleKey == "custom:tier-2");
        // Seeded roles are is_system=1 and never appear in the custom list.
        Assert.DoesNotContain(listed, r => r.RoleKey == "org-owner");
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task DuplicateCustomRoleKeyRejected()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        var admin = new MySqlAuthzAdministrationStore(db.ConnectionFactory, new UlidFactory());
        Assert.True((await admin.CreateCustomRoleAsync("custom:dup", "Dup", "", [], "actor1")).IsOk);
        Assert.Equal(AuthzWriteStatus.Conflict,
            (await admin.CreateCustomRoleAsync("custom:dup", "Dup", "", [], "actor1")).Status);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task NonAuthorableAndMalformedCreatesRejectedAndWriteNothing()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var admin = new MySqlAuthzAdministrationStore(db.ConnectionFactory, new UlidFactory());

        // An excluded privileged key is rejected (subsumed by the positive allow-list).
        Assert.Equal(AuthzWriteStatus.Invalid,
            (await admin.CreateCustomRoleAsync("custom:esc", "Esc", "", [AuthzActions.SystemAdmin], "actor1")).Status);
        // An unprefixed key is not an authorable role key.
        Assert.Equal(AuthzWriteStatus.Invalid,
            (await admin.CreateCustomRoleAsync("auditor", "A", "", [AuthzActions.OrgRead], "actor1")).Status);
        // Blank title.
        Assert.Equal(AuthzWriteStatus.Invalid,
            (await admin.CreateCustomRoleAsync("custom:blank", "  ", "", [AuthzActions.OrgRead], "actor1")).Status);
        // Over-length title / description.
        Assert.Equal(AuthzWriteStatus.Invalid,
            (await admin.CreateCustomRoleAsync("custom:long", new string('t', 191), "", [], "actor1")).Status);
        Assert.Equal(AuthzWriteStatus.Invalid,
            (await admin.CreateCustomRoleAsync("custom:longd", "T", new string('d', 513), [], "actor1")).Status);

        Assert.Equal(0, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM authz_roles WHERE role_key LIKE 'custom:%';"));
        // The unprefixed 'auditor' key must not have been written under its own name either.
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM authz_roles WHERE role_key = 'auditor';"));
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM authz_audit_events WHERE event_type LIKE 'authz.role.%';"));
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task UpdateReplacesPermissionsAndWritesAudit()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var admin = new MySqlAuthzAdministrationStore(db.ConnectionFactory, new UlidFactory());
        await admin.CreateCustomRoleAsync("custom:c", "C", "old", [AuthzActions.OrgRead], "actor1");

        Assert.True((await admin.UpdateCustomRoleAsync(
            "custom:c", "C2", "new", [AuthzActions.ComplianceRead, AuthzActions.ComplianceScopeWrite], "actor1")).IsOk);

        var store = new MySqlAuthzStore(db.ConnectionFactory);
        var loaded = await store.GetRoleAsync("custom:c");
        Assert.Equal("C2", loaded!.Role.Title);
        Assert.Equal("new", loaded.Role.Description);
        Assert.Equal([AuthzActions.ComplianceRead, AuthzActions.ComplianceScopeWrite], loaded.PermissionKeys);
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM authz_audit_events WHERE event_type = 'authz.role.update' AND resource_id = 'custom:c';"));
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task RejectedUpdateLeavesRoleUnchangedAndWritesNoAudit()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var admin = new MySqlAuthzAdministrationStore(db.ConnectionFactory, new UlidFactory());
        await admin.CreateCustomRoleAsync("custom:c", "C", "desc", [AuthzActions.OrgRead], "actor1");

        // A non-authorable key in the update is rejected and writes nothing.
        Assert.Equal(AuthzWriteStatus.Invalid,
            (await admin.UpdateCustomRoleAsync("custom:c", "C", "desc", [AuthzActions.SystemAdmin], "actor1")).Status);

        var store = new MySqlAuthzStore(db.ConnectionFactory);
        var loaded = await store.GetRoleAsync("custom:c");
        Assert.Equal("C", loaded!.Role.Title);
        Assert.Equal([AuthzActions.OrgRead], loaded.PermissionKeys);
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM authz_audit_events WHERE event_type = 'authz.role.update';"));
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task SeededRoleUpdateAndDeleteRejected()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        var admin = new MySqlAuthzAdministrationStore(db.ConnectionFactory, new UlidFactory());
        Assert.Equal(AuthzWriteStatus.Invalid,
            (await admin.UpdateCustomRoleAsync("org-owner", "X", "", [AuthzActions.OrgRead], "actor1")).Status);
        Assert.Equal(AuthzWriteStatus.Invalid,
            (await admin.DeleteCustomRoleAsync("org-owner", "actor1")).Status);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task UnknownRoleUpdateAndDeleteReturnNotFound()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        var admin = new MySqlAuthzAdministrationStore(db.ConnectionFactory, new UlidFactory());
        Assert.Equal(AuthzWriteStatus.NotFound,
            (await admin.UpdateCustomRoleAsync("custom:ghost", "X", "", [], "actor1")).Status);
        Assert.Equal(AuthzWriteStatus.NotFound,
            (await admin.DeleteCustomRoleAsync("custom:ghost", "actor1")).Status);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task InUseCustomRoleDeleteBlockedUnusedDeleteCascadesAndAudits()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await InsertUserAsync(conn, "u1", "member");
        await InsertOrgAsync(conn, "org1");

        var admin = new MySqlAuthzAdministrationStore(db.ConnectionFactory, new UlidFactory());
        await admin.CreateCustomRoleAsync("custom:c", "C", "", [AuthzActions.OrgRead], "actor1");
        Assert.True((await admin.AssignOrganisationRoleAsync("u1", "custom:c", "org1")).IsOk);

        // In use -> Conflict; the role and its permission rows survive.
        Assert.Equal(AuthzWriteStatus.Conflict, (await admin.DeleteCustomRoleAsync("custom:c", "actor1")).Status);
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM authz_roles WHERE role_key = 'custom:c';"));

        // Revoke, then the delete succeeds, cascades permission rows, and writes an audit row.
        Assert.True((await admin.RevokeOrganisationRoleAsync("u1", "custom:c", "org1", "actor1")).IsOk);
        Assert.True((await admin.DeleteCustomRoleAsync("custom:c", "actor1")).IsOk);
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM authz_roles WHERE role_key = 'custom:c';"));
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM authz_role_permissions WHERE role_key = 'custom:c';"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM authz_audit_events WHERE event_type = 'authz.role.delete' AND resource_id = 'custom:c';"));
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task AssigningCustomRoleExpandsAssigneeFacts()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await InsertUserAsync(conn, "u1", "member");
        await InsertOrgAsync(conn, "org1");

        var admin = new MySqlAuthzAdministrationStore(db.ConnectionFactory, new UlidFactory());
        await admin.CreateCustomRoleAsync(
            "custom:c", "C", "", [AuthzActions.OrgRead, AuthzActions.ComplianceRead], "actor1");
        Assert.True((await admin.AssignOrganisationRoleAsync("u1", "custom:c", "org1")).IsOk);

        var facts = await new MySqlAuthzStore(db.ConnectionFactory).LoadPrincipalFactsAsync("u1");
        Assert.Contains(facts.OrgGrants, g => g.PermissionKey == AuthzActions.OrgRead && g.OrganisationId == "org1");
        Assert.Contains(facts.OrgGrants, g => g.PermissionKey == AuthzActions.ComplianceRead && g.OrganisationId == "org1");
    }

    private static async Task<long> SuperAdminCountAsync(MySqlTestDatabase db)
    {
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM authz_system_role_assignments WHERE role_key = 'super-admin';");
    }
}

/// <summary>Returns a fixed id, so a test can force an audit-insert PK collision inside the write transaction.</summary>
internal sealed class FixedUlidFactory(string id) : IUlidFactory
{
    public string NewId() => id;

    public string Parse(string value) => value;
}

/// <summary>Reads the embedded 010 migration SQL so a test can replay it for idempotency/backfill checks.</summary>
internal static class Migration010Reader
{
    public static async Task<string> ReadMigration010Async(this MySqlConnection _)
    {
        var asm = typeof(IMigrationRunner).Assembly;
        var name = asm.GetManifestResourceNames().Single(n => n.EndsWith("010_authorization.sql", StringComparison.Ordinal));
        await using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}
