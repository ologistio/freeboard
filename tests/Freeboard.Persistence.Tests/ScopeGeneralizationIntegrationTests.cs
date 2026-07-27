using Dapper;
using Freeboard.Core.GitOps;
using Freeboard.Persistence;
using Freeboard.Persistence.Auth;
using Freeboard.Persistence.GitOps;
using Freeboard.Persistence.System;
using Freeboard.TestInfrastructure;
using MySqlConnector;

namespace Freeboard.Persistence.Tests;

/// <summary>
/// Integration tests for the scope generalization (migration 020, the merged scopes table, the
/// single-target CHECK, the three unique keys and target RESTRICT FKs, the whole-set-replace sync with the
/// DB-accurate dangling-subject warning, and the write-store target-column isolation) against a real MySQL
/// discovered via FREEBOARD_TEST_DB. Each test SKIPS cleanly when the env var is absent.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class ScopeGeneralizationIntegrationTests
{
    // The exact provenance marker migration 020 writes into a copied Out row with no recorded rationale.
    private const string ProvenanceMarker = "Migrated legacy Out rule; justification was not recorded and requires review.";

    private static async Task<MySqlTestDatabase> RequireDbAsync()
    {
        var db = await MySqlTestDatabase.TryCreateAsync();
        Skip.If(db is null, $"{MySqlTestDatabase.EnvVar} not set; skipping MySQL integration test.");
        return db!;
    }

    private static Task MigrateAsync(MySqlTestDatabase db) =>
        new MySqlMigrationRunner(db.ConnectionFactory, typeof(IMigrationRunner).Assembly).ApplyPendingAsync();

    // Applies every migration up to and including maxOrdinal over a raw connection, so a test can seed the
    // pre-020 legacy tables before running 020 itself.
    private static async Task MigrateThroughOrdinalAsync(MySqlConnection conn, int maxOrdinal)
    {
        foreach (var migration in MigrationCatalog.Load(typeof(IMigrationRunner).Assembly)
            .Where(m => m.Ordinal <= maxOrdinal))
        {
            await conn.ExecuteAsync(migration.Sql);
        }
    }

    private static string MigrationSql(int ordinal) =>
        MigrationCatalog.Load(typeof(IMigrationRunner).Assembly).Single(m => m.Ordinal == ordinal).Sql;

    private static GitOpsConfig Config(
        IEnumerable<Standard>? standards = null,
        IEnumerable<Requirement>? requirements = null,
        IEnumerable<Control>? controls = null,
        IEnumerable<Asset>? assets = null,
        IEnumerable<Scope>? scopes = null) => new()
        {
            Standards = standards?.ToList() ?? [],
            Requirements = requirements?.ToList() ?? [],
            Controls = controls?.ToList() ?? [],
            Assets = assets?.ToList() ?? [],
            Scopes = scopes?.ToList() ?? [],
        };

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

    private static Control Ctrl(string id, string[] mapsTo) =>
        new() { Id = id, Title = "T", ApiVersion = "v1", MapsTo = [.. mapsTo] };

    private static Asset Org(string id, string type = "Company", string? parent = null) =>
        new() { Id = id, Title = "T", ApiVersion = "v1", Type = type, Source = "declared", Parent = parent ?? string.Empty };

    private static Asset Vnd(string id, string? owner = null) =>
        new() { Id = id, Title = "T", ApiVersion = "v1", Type = "Vendor", Source = "declared", Owner = owner ?? string.Empty };

    private static Scope Scope(
        string id, string subject, string? standard = null, string? requirement = null, string? control = null,
        string disposition = "In", string? justification = null) =>
        new()
        {
            Id = id,
            Title = "T",
            ApiVersion = "v1",
            Subject = subject,
            Standard = standard ?? string.Empty,
            Requirement = requirement ?? string.Empty,
            Control = control ?? string.Empty,
            Disposition = disposition,
            Justification = justification ?? (disposition == "Out" ? "Compensating control in place." : string.Empty),
        };

    private static NewMachineObservation Obs(string org, string externalId, string serial) =>
        new(org, "fleetdm", externalId, serial, null, null);

    // Migration 020 merges the three legacy tables into one scopes table, preserving/backfilling
    // justification per source shape, and drops requirement_scopes/vendor_scopes.
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task Migration020MergesThreeTablesAndBackfillsProvenanceMarker()
    {
        await using var db = await RequireDbAsync();
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await MigrateThroughOrdinalAsync(conn, 19);

        // Catalogue and subject rows the legacy scope FKs (target + org/vendor) resolve against.
        await conn.ExecuteAsync(
            "INSERT INTO standards (id, api_version, title, created_at, updated_at) VALUES ('std-a', 'v1', 'S', NOW(6), NOW(6));");
        await conn.ExecuteAsync(
            "INSERT INTO requirements (id, api_version, title, standard_id, theme, statement, citation_label, citation_url, created_at, updated_at) "
            + "VALUES ('req-a', 'v1', 'R', 'std-a', 'T', 'S', 'L', 'https://example.com/r', NOW(6), NOW(6));");
        await conn.ExecuteAsync(
            "INSERT INTO controls (id, api_version, title, created_at, updated_at) VALUES ('ctrl-a', 'v1', 'C', NOW(6), NOW(6));");
        await conn.ExecuteAsync(
            "INSERT INTO assets (id, type, source, api_version, title, created_at, updated_at) VALUES "
            + "('org-a', 'Company', 'declared', 'v1', 'O', NOW(6), NOW(6)), "
            + "('org-b', 'Company', 'declared', 'v1', 'O', NOW(6), NOW(6)), "
            + "('vendor-a', 'Vendor', 'declared', 'v1', 'V', NOW(6), NOW(6));");

        // Legacy org scopes (no justification column): one Out (backfilled with the marker) and one In (NULL).
        await conn.ExecuteAsync(
            "INSERT INTO scopes (id, api_version, title, organisation_id, standard_id, disposition, created_at, updated_at) VALUES "
            + "('sc-out', 'v1', 'T', 'org-a', 'std-a', 'Out', NOW(6), NOW(6)), "
            + "('sc-in', 'v1', 'T', 'org-b', 'std-a', 'In', NOW(6), NOW(6));");
        // Legacy requirement scope (no justification column): an Out backfilled with the marker.
        await conn.ExecuteAsync(
            "INSERT INTO requirement_scopes (id, api_version, title, organisation_id, requirement_id, disposition, created_at, updated_at) "
            + "VALUES ('rs-out', 'v1', 'T', 'org-a', 'req-a', 'Out', NOW(6), NOW(6));");
        // Legacy vendor scopes (real justification column): one Out with a recorded rationale (preserved) and
        // one blank Out (backfilled with the marker).
        await conn.ExecuteAsync(
            "INSERT INTO vendor_scopes (id, api_version, title, vendor_id, requirement_id, control_id, disposition, justification, created_at, updated_at) VALUES "
            + "('vs-real', 'v1', 'T', 'vendor-a', 'req-a', NULL, 'Out', 'Supports MFA but not SSO.', NOW(6), NOW(6)), "
            + "('vs-blank', 'v1', 'T', 'vendor-a', NULL, 'ctrl-a', 'Out', '   ', NOW(6), NOW(6));");

        await conn.ExecuteAsync(MigrationSql(20));

        // The legacy tables are gone; the unified scopes table has the CHECK, the three unique keys, and the
        // three collision-free target FKs.
        var tables = (await conn.QueryAsync<string>(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = DATABASE();"))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("scopes", tables);
        Assert.DoesNotContain("requirement_scopes", tables);
        Assert.DoesNotContain("vendor_scopes", tables);

        Assert.Equal(1, await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.table_constraints "
            + "WHERE table_schema = DATABASE() AND table_name = 'scopes' AND constraint_type = 'CHECK' "
            + "AND constraint_name = 'ck_scopes_single_target';"));
        foreach (var index in new[] { "uq_scopes_subject_standard", "uq_scopes_subject_requirement", "uq_scopes_subject_control" })
        {
            Assert.True(await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM information_schema.statistics "
                + "WHERE table_schema = DATABASE() AND table_name = 'scopes' AND index_name = @Index AND non_unique = 0;",
                new { Index = index }) >= 1);
        }
        var targetFks = (await conn.QueryAsync<string>(
            "SELECT constraint_name FROM information_schema.referential_constraints "
            + "WHERE constraint_schema = DATABASE() AND table_name = 'scopes' ORDER BY constraint_name;"))
            .ToArray();
        Assert.Equal(["fk_scopes_v2_control", "fk_scopes_v2_requirement", "fk_scopes_v2_standard"], targetFks);

        // All five rows copied with their subject mapped from organisation_id/vendor_id and one target set.
        var rows = (await conn.QueryAsync<(string Id, string Subject, string? Standard, string? Requirement, string? Control, string Disposition, string? Justification)>(
            "SELECT id AS Id, subject_id AS Subject, standard_id AS Standard, requirement_id AS Requirement, "
            + "control_id AS Control, disposition AS Disposition, justification AS Justification FROM scopes ORDER BY id;"))
            .ToDictionary(r => r.Id);
        Assert.Equal(5, rows.Count);

        // Org Out from scopes -> marker; org In from scopes -> NULL.
        Assert.Equal(("org-a", "std-a", ProvenanceMarker), (rows["sc-out"].Subject, rows["sc-out"].Standard, rows["sc-out"].Justification));
        Assert.Null(rows["sc-in"].Justification);
        // Org Out from requirement_scopes -> marker, requirement target only.
        Assert.Equal(("org-a", "req-a", ProvenanceMarker), (rows["rs-out"].Subject, rows["rs-out"].Requirement, rows["rs-out"].Justification));
        Assert.Null(rows["rs-out"].Standard);
        // Vendor Out with a real justification -> preserved unchanged.
        Assert.Equal(("vendor-a", "req-a", "Supports MFA but not SSO."), (rows["vs-real"].Subject, rows["vs-real"].Requirement, rows["vs-real"].Justification));
        // Blank vendor Out -> marker, control target only.
        Assert.Equal(("vendor-a", "ctrl-a", ProvenanceMarker), (rows["vs-blank"].Subject, rows["vs-blank"].Control, rows["vs-blank"].Justification));
        Assert.Null(rows["vs-blank"].Requirement);
    }

    // Colliding ids across the three source tables fail 020 loudly on the duplicate primary key.
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task Migration020FailsOnCollidingScopeIds()
    {
        await using var db = await RequireDbAsync();
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await MigrateThroughOrdinalAsync(conn, 19);

        await conn.ExecuteAsync(
            "INSERT INTO standards (id, api_version, title, created_at, updated_at) VALUES ('std-a', 'v1', 'S', NOW(6), NOW(6));");
        await conn.ExecuteAsync(
            "INSERT INTO requirements (id, api_version, title, standard_id, theme, statement, citation_label, citation_url, created_at, updated_at) "
            + "VALUES ('req-a', 'v1', 'R', 'std-a', 'T', 'S', 'L', 'https://example.com/r', NOW(6), NOW(6));");
        await conn.ExecuteAsync(
            "INSERT INTO assets (id, type, source, api_version, title, created_at, updated_at) VALUES ('org-a', 'Company', 'declared', 'v1', 'O', NOW(6), NOW(6));");

        // The same id in two source tables collides on the unified primary key.
        await conn.ExecuteAsync(
            "INSERT INTO scopes (id, api_version, title, organisation_id, standard_id, disposition, created_at, updated_at) "
            + "VALUES ('dup', 'v1', 'T', 'org-a', 'std-a', 'In', NOW(6), NOW(6));");
        await conn.ExecuteAsync(
            "INSERT INTO requirement_scopes (id, api_version, title, organisation_id, requirement_id, disposition, created_at, updated_at) "
            + "VALUES ('dup', 'v1', 'T', 'org-a', 'req-a', 'In', NOW(6), NOW(6));");

        var ex = await Assert.ThrowsAsync<MySqlException>(() => conn.ExecuteAsync(MigrationSql(20)));
        Assert.Equal(MySqlErrorCode.DuplicateKeyEntry, ex.ErrorCode);
    }

    // The single-target CHECK rejects a zero-target row and a two-target row written directly.
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task ScopesCheckRejectsZeroTargetAndTwoTargetRows()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var importer = new MySqlGitOpsImporter(db.ConnectionFactory);
        await importer.ImportAsync(Config(
            [Std("std-a")], [Req("req-a", "std-a")], assets: [Org("org-a")]));

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        // No target column set: rejected by ck_scopes_single_target.
        await Assert.ThrowsAsync<MySqlException>(() => conn.ExecuteAsync(
            "INSERT INTO scopes (id, api_version, title, subject_id, disposition, created_at, updated_at) "
            + "VALUES ('sc-none', 'v1', 'T', 'org-a', 'In', NOW(6), NOW(6));"));

        // Two target columns set: rejected by the same CHECK (both targets real, so only the CHECK fires).
        await Assert.ThrowsAsync<MySqlException>(() => conn.ExecuteAsync(
            "INSERT INTO scopes (id, api_version, title, subject_id, standard_id, requirement_id, disposition, created_at, updated_at) "
            + "VALUES ('sc-two', 'v1', 'T', 'org-a', 'std-a', 'req-a', 'In', NOW(6), NOW(6));"));
    }

    // Each NULL-distinct composite unique key rejects a duplicate (subject, target) pair.
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task ScopesUniqueKeysRejectDuplicatePairPerTarget()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var importer = new MySqlGitOpsImporter(db.ConnectionFactory);
        await importer.ImportAsync(Config(
            [Std("std-a")], [Req("req-a", "std-a")], [Ctrl("ctrl-a", ["req-a"])], [Org("org-a")],
            [
                Scope("sc-std", "org-a", standard: "std-a"),
                Scope("sc-req", "org-a", requirement: "req-a"),
                Scope("sc-ctrl", "org-a", control: "ctrl-a"),
            ]));

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        // A second row under a DIFFERENT id but the same (subject, target) pair violates each unique key.
        await Assert.ThrowsAsync<MySqlException>(() => conn.ExecuteAsync(
            "INSERT INTO scopes (id, api_version, title, subject_id, standard_id, disposition, created_at, updated_at) "
            + "VALUES ('dup-std', 'v1', 'T', 'org-a', 'std-a', 'In', NOW(6), NOW(6));"));
        await Assert.ThrowsAsync<MySqlException>(() => conn.ExecuteAsync(
            "INSERT INTO scopes (id, api_version, title, subject_id, requirement_id, disposition, created_at, updated_at) "
            + "VALUES ('dup-req', 'v1', 'T', 'org-a', 'req-a', 'In', NOW(6), NOW(6));"));
        await Assert.ThrowsAsync<MySqlException>(() => conn.ExecuteAsync(
            "INSERT INTO scopes (id, api_version, title, subject_id, control_id, disposition, created_at, updated_at) "
            + "VALUES ('dup-ctrl', 'v1', 'T', 'org-a', 'ctrl-a', 'In', NOW(6), NOW(6));"));
    }

    // Each target FK is ON DELETE RESTRICT, so a targeted catalogue row cannot be dropped while a
    // scope references it.
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task ScopeTargetRestrictBlocksDeletingReferencedCatalogueRow()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var importer = new MySqlGitOpsImporter(db.ConnectionFactory);
        await importer.ImportAsync(Config(
            [Std("std-a")], [Req("req-a", "std-a")], [Ctrl("ctrl-a", ["req-a"])], [Org("org-a")],
            [
                Scope("sc-std", "org-a", standard: "std-a"),
                Scope("sc-req", "org-a", requirement: "req-a"),
                Scope("sc-ctrl", "org-a", control: "ctrl-a"),
            ]));

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        await Assert.ThrowsAsync<MySqlException>(() => conn.ExecuteAsync("DELETE FROM standards WHERE id = 'std-a';"));
        // A requirement is referenced by both a requirement-target scope and its owning standard; delete the
        // control-target and standard-target scopes first so only the requirement RESTRICT can fire, then the
        // control's.
        await Assert.ThrowsAsync<MySqlException>(() => conn.ExecuteAsync("DELETE FROM requirements WHERE id = 'req-a';"));
        await Assert.ThrowsAsync<MySqlException>(() => conn.ExecuteAsync("DELETE FROM controls WHERE id = 'ctrl-a';"));
    }

    // A whole-set-replace sync round-trips the unified scopes and hard-removes an absent scope.
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task SyncRoundTripsUnifiedScopesAndHardRemovesAbsent()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var importer = new MySqlGitOpsImporter(db.ConnectionFactory);
        var store = new MySqlComplianceStore(db.ConnectionFactory);

        await importer.ImportAsync(Config(
            [Std("std-a")], [Req("req-a", "std-a")], assets: [Org("org-a")],
            scopes: [Scope("sc-std", "org-a", standard: "std-a"), Scope("sc-req", "org-a", requirement: "req-a")]));
        Assert.Equal(["sc-req", "sc-std"], (await store.GetScopesAsync()).Select(s => s.Id).ToArray());

        // A re-sync dropping sc-req hard-removes it (the whole-set replace is the prune).
        var result = await importer.ImportAsync(Config(
            [Std("std-a")], [Req("req-a", "std-a")], assets: [Org("org-a")],
            scopes: [Scope("sc-std", "org-a", standard: "std-a")]));
        Assert.Equal(["sc-std"], (await store.GetScopesAsync()).Select(s => s.Id).ToArray());
        Assert.Empty(result.UnresolvedScopeSubjects);
    }

    // Removing a scope's subject asset does not fail the sync (subject_id has no FK); the scope
    // survives with a dangling subject and the importer reports it as unresolved.
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task RemovingScopeSubjectAssetLeavesDanglingScopeAndWarns()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var importer = new MySqlGitOpsImporter(db.ConnectionFactory);
        var store = new MySqlComplianceStore(db.ConnectionFactory);

        await importer.ImportAsync(Config(
            [Std("std-a")], assets: [Org("org-a")], scopes: [Scope("sc-1", "org-a", standard: "std-a")]));

        // Re-sync without org-a: the declared-asset prune removes it, but the scope (subject still org-a)
        // is not blocked and simply dangles. The importer's post-write check reports the dangling subject.
        var result = await importer.ImportAsync(Config(
            [Std("std-a")], scopes: [Scope("sc-1", "org-a", standard: "std-a")]));

        Assert.Empty(await store.GetOrganisationsAsync());
        var scope = Assert.Single(await store.GetScopesAsync());
        Assert.Equal("org-a", scope.Subject);
        Assert.Contains("org-a", result.UnresolvedScopeSubjects);
    }

    // The importer result reports a scope whose Machine subject is absent OR present only as a discovered
    // Retired row as unresolved, while a live discovered Machine subject is NOT reported. Only the DB can
    // evaluate this - Core sees only the authored asset set.
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task SyncWarningReportsAbsentAndRetiredMachineSubjectsButNotLive()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var importer = new MySqlGitOpsImporter(db.ConnectionFactory);
        var writes = new MySqlAssetWriteStore(db.ConnectionFactory, new UlidFactory());

        // A declared org so the discovered machines have a parent, then a live and a retired discovered machine.
        await importer.ImportAsync(Config([Std("std-a")], assets: [Org("org-a")]));
        var live = await writes.UpsertMachineFromSourceAsync(Obs("org-a", "host-live", "SN-LIVE"));
        var retired = await writes.UpsertMachineFromSourceAsync(Obs("org-a", "host-ret", "SN-RET"));
        Assert.Equal(AssetUpsertStatus.Created, live.Status);
        Assert.Equal(AssetUpsertStatus.Created, retired.Status);

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            "UPDATE assets SET state = 'Retired' WHERE id = @Id;", new { Id = retired.AssetId });

        // One scope per subject: live machine (resolves), retired machine (unresolved, the retired
        // discovered branch), and an id naming no asset at all (unresolved).
        var result = await importer.ImportAsync(Config(
            [Std("std-a")], assets: [Org("org-a")],
            scopes:
            [
                Scope("sc-live", live.AssetId!, standard: "std-a"),
                Scope("sc-retired", retired.AssetId!, standard: "std-a"),
                Scope("sc-absent", "no-such-subject", standard: "std-a"),
            ]));

        Assert.Contains("no-such-subject", result.UnresolvedScopeSubjects);
        Assert.Contains(retired.AssetId!, result.UnresolvedScopeSubjects);
        Assert.DoesNotContain(live.AssetId!, result.UnresolvedScopeSubjects);
    }

    // The write store confines each app route to its own target column. A wrong-kind id is a no-op
    // not-found on both DELETE and PUT, a control-target row is unreachable from both routes, and a
    // brand-new id still creates a row of the route's own target kind (must not regress creation).
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task WriteStoreConfinesEachRouteToItsTargetColumnWithoutBreakingCreate()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var importer = new MySqlGitOpsImporter(db.ConnectionFactory);
        var store = new MySqlComplianceStore(db.ConnectionFactory);
        var writeStore = new MySqlComplianceWriteStore(db.ConnectionFactory);

        // Seed a standard-target, a requirement-target, and a control-target scope, all org-a subjects.
        await importer.ImportAsync(Config(
            [Std("std-a")], [Req("req-a", "std-a")], [Ctrl("ctrl-a", ["req-a"])], [Org("org-a"), Org("org-b")],
            [
                Scope("sc-std", "org-a", standard: "std-a"),
                Scope("sc-req", "org-a", requirement: "req-a"),
                Scope("sc-ctrl", "org-a", control: "ctrl-a"),
            ]));

        // The standard route cannot reach the requirement-target or control-target row (delete or PUT).
        Assert.True((await writeStore.DeleteScopeAsync("sc-req", expectedOwner: "org-a")).IsNotFound);
        Assert.True((await writeStore.DeleteScopeAsync("sc-ctrl", expectedOwner: "org-a")).IsNotFound);
        Assert.True((await writeStore.UpsertScopeDispositionAsync("sc-req", "T", "org-a", "std-a", "In")).IsNotFound);
        Assert.True((await writeStore.UpsertScopeDispositionAsync("sc-ctrl", "T", "org-a", "std-a", "In")).IsNotFound);

        // The requirement route cannot reach the standard-target or control-target row (delete or PUT).
        Assert.True((await writeStore.DeleteRequirementScopeAsync("sc-std", expectedOwner: "org-a")).IsNotFound);
        Assert.True((await writeStore.DeleteRequirementScopeAsync("sc-ctrl", expectedOwner: "org-a")).IsNotFound);
        Assert.True((await writeStore.UpsertRequirementScopeDispositionAsync("sc-std", "T", "org-a", "req-a", "In")).IsNotFound);
        Assert.True((await writeStore.UpsertRequirementScopeDispositionAsync("sc-ctrl", "T", "org-a", "req-a", "In")).IsNotFound);

        // Nothing was mutated by the no-op wrong-kind writes: the three seeded rows keep their targets.
        var before = (await store.GetScopesAsync()).ToDictionary(s => s.Id);
        Assert.Equal(3, before.Count);
        Assert.Equal("std-a", before["sc-std"].Standard);
        Assert.Equal("req-a", before["sc-req"].Requirement);
        Assert.Equal("ctrl-a", before["sc-ctrl"].Control);

        // A brand-new id still creates a row of the route's own target kind (no 404 regression).
        Assert.True((await writeStore.UpsertScopeDispositionAsync("new-std", "T", "org-b", "std-a", "In")).Ok);
        Assert.True((await writeStore.UpsertRequirementScopeDispositionAsync("new-req", "T", "org-b", "req-a", "In")).Ok);

        var after = (await store.GetScopesAsync()).ToDictionary(s => s.Id);
        Assert.Equal("std-a", after["new-std"].Standard);
        Assert.Null(after["new-std"].Requirement);
        Assert.Equal("req-a", after["new-req"].Requirement);
        Assert.Null(after["new-req"].Standard);
    }

    // The delete matches the caller's authorized owner: a delete whose expected owner differs from the row's
    // current subject removes nothing (the row moved to an org the caller was not authorized for), while a
    // delete whose expected owner matches still removes the row. This closes the cross-org-move TOCTOU: a
    // caller authorized against org-a cannot delete a row now owned by org-b.
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task DeleteMatchesExpectedOwnerAndSkipsAMovedRow()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var importer = new MySqlGitOpsImporter(db.ConnectionFactory);
        var store = new MySqlComplianceStore(db.ConnectionFactory);
        var writeStore = new MySqlComplianceWriteStore(db.ConnectionFactory);

        await importer.ImportAsync(Config(
            [Std("std-a")], [Req("req-a", "std-a")], assets: [Org("org-a"), Org("org-b")],
            scopes: [Scope("sc-std", "org-a", standard: "std-a"), Scope("sc-req", "org-a", requirement: "req-a")]));

        // Expected owner org-b does not match the org-a subject: nothing is deleted on either route.
        Assert.True((await writeStore.DeleteScopeAsync("sc-std", expectedOwner: "org-b")).IsNotFound);
        Assert.True((await writeStore.DeleteRequirementScopeAsync("sc-req", expectedOwner: "org-b")).IsNotFound);
        Assert.Equal(["sc-req", "sc-std"], (await store.GetScopesAsync()).Select(s => s.Id).OrderBy(id => id).ToArray());

        // Expected owner org-a matches the subject: the delete removes the row on each route.
        Assert.True((await writeStore.DeleteScopeAsync("sc-std", expectedOwner: "org-a")).Ok);
        Assert.True((await writeStore.DeleteRequirementScopeAsync("sc-req", expectedOwner: "org-a")).Ok);
        Assert.Empty(await store.GetScopesAsync());
    }

    // App-managed scope writes are restricted to Company/Department subjects: a Vendor or Machine subject
    // scope is GitOps-write-only, so an app PUT-retarget or DELETE resolves it to not-found and leaves it
    // unmutated, while a Company/Department subject row still updates and deletes and a new-id create still
    // succeeds.
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task AppRoutesRejectVendorAndMachineSubjectScopesAsNotFound()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var importer = new MySqlGitOpsImporter(db.ConnectionFactory);
        var store = new MySqlComplianceStore(db.ConnectionFactory);
        var writeStore = new MySqlComplianceWriteStore(db.ConnectionFactory);
        var assetWrites = new MySqlAssetWriteStore(db.ConnectionFactory, new UlidFactory());

        // A declared org and vendor, then a discovered machine under the org to subject its scopes.
        await importer.ImportAsync(Config([Std("std-a")], [Req("req-a", "std-a")], assets: [Org("org-a"), Org("org-b"), Vnd("vendor-a", owner: "org-a")]));
        var machine = await assetWrites.UpsertMachineFromSourceAsync(Obs("org-a", "host-m", "SN-M"));
        Assert.Equal(AssetUpsertStatus.Created, machine.Status);
        var machineId = machine.AssetId!;

        // One whole-set import seeding org, vendor, and machine subject rows on both app-writable targets.
        await importer.ImportAsync(Config(
            [Std("std-a")], [Req("req-a", "std-a")], assets: [Org("org-a"), Org("org-b"), Vnd("vendor-a", owner: "org-a")],
            scopes:
            [
                Scope("sc-org-std", "org-a", standard: "std-a"),
                Scope("sc-org-req", "org-a", requirement: "req-a"),
                Scope("sc-vnd-std", "vendor-a", standard: "std-a"),
                Scope("sc-vnd-req", "vendor-a", requirement: "req-a"),
                Scope("sc-mac-std", machineId, standard: "std-a"),
                Scope("sc-mac-req", machineId, requirement: "req-a"),
            ]));

        // PUT-retarget on a Vendor/Machine subject row is not-found on both routes.
        Assert.True((await writeStore.UpsertScopeDispositionAsync("sc-vnd-std", "T", "org-a", "std-a", "In", expectedCurrentOrganisation: "org-a")).IsNotFound);
        Assert.True((await writeStore.UpsertScopeDispositionAsync("sc-mac-std", "T", "org-a", "std-a", "In", expectedCurrentOrganisation: "org-a")).IsNotFound);
        Assert.True((await writeStore.UpsertRequirementScopeDispositionAsync("sc-vnd-req", "T", "org-a", "req-a", "In", expectedCurrentOrganisation: "org-a")).IsNotFound);
        Assert.True((await writeStore.UpsertRequirementScopeDispositionAsync("sc-mac-req", "T", "org-a", "req-a", "In", expectedCurrentOrganisation: "org-a")).IsNotFound);

        // DELETE on a Vendor/Machine subject row is not-found on both routes (owner matches the subject, so
        // the subject-type guard - not an owner mismatch - is what yields not-found).
        Assert.True((await writeStore.DeleteScopeAsync("sc-vnd-std", expectedOwner: "vendor-a")).IsNotFound);
        Assert.True((await writeStore.DeleteScopeAsync("sc-mac-std", expectedOwner: machineId)).IsNotFound);
        Assert.True((await writeStore.DeleteRequirementScopeAsync("sc-vnd-req", expectedOwner: "vendor-a")).IsNotFound);
        Assert.True((await writeStore.DeleteRequirementScopeAsync("sc-mac-req", expectedOwner: machineId)).IsNotFound);

        // The four Vendor/Machine subject rows are untouched by the rejected writes.
        var mid = (await store.GetScopesAsync()).ToDictionary(s => s.Id);
        Assert.Equal("std-a", mid["sc-vnd-std"].Standard);
        Assert.Equal("req-a", mid["sc-vnd-req"].Requirement);
        Assert.Equal("std-a", mid["sc-mac-std"].Standard);
        Assert.Equal("req-a", mid["sc-mac-req"].Requirement);

        // A Company/Department subject row still updates (requirement route) and deletes (standard route).
        Assert.True((await writeStore.UpsertRequirementScopeDispositionAsync(
            "sc-org-req", "Updated", "org-a", "req-a", "Out", "Compensating control.", "org-a")).Ok);
        Assert.True((await writeStore.DeleteScopeAsync("sc-org-std", expectedOwner: "org-a")).Ok);

        // A brand-new id still creates a Company/Department subject scope.
        Assert.True((await writeStore.UpsertScopeDispositionAsync("new-std", "T", "org-b", "std-a", "In")).Ok);

        var after = (await store.GetScopesAsync()).ToDictionary(s => s.Id);
        Assert.Equal("Updated", after["sc-org-req"].Title);
        Assert.Equal("Out", after["sc-org-req"].Disposition);
        Assert.False(after.ContainsKey("sc-org-std"));
        Assert.Equal("std-a", after["new-std"].Standard);
    }
}
