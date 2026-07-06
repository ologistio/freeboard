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
[Trait("Category", TestCategories.Integration)]
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
        IEnumerable<Organisation>? organisations = null,
        IEnumerable<Scope>? scopes = null,
        IEnumerable<Requirement>? requirements = null,
        IEnumerable<RequirementScope>? requirementScopes = null) => new()
        {
            Standards = standards.ToList(),
            Requirements = requirements?.ToList() ?? [],
            Controls = controls.ToList(),
            Organisations = organisations?.ToList() ?? [],
            Scopes = scopes?.ToList() ?? [],
            RequirementScopes = requirementScopes?.ToList() ?? [],
        };

    private static Standard Std(
        string id, string title = "T", string apiVersion = "v1", string version = "1.0", string authority = "Example Authority") =>
        new() { Id = id, Title = title, ApiVersion = apiVersion, Version = version, Authority = authority };

    private static Requirement Req(
        string id, string standard, string theme = "Theme", string apiVersion = "v1", string? guidance = null) =>
        new()
        {
            Id = id,
            Title = "T",
            ApiVersion = apiVersion,
            Standard = standard,
            Theme = theme,
            Statement = "Do the thing.",
            Guidance = guidance ?? string.Empty,
            CitationLabel = "Source",
            CitationUrl = "https://example.com/" + id,
        };

    private static Control Ctrl(string id, string[] mapsTo, string title = "T", string apiVersion = "v1") =>
        new() { Id = id, Title = title, ApiVersion = apiVersion, MapsTo = [.. mapsTo] };

    private static Organisation Org(string id, string kind = "Company", string? parent = null, string title = "T", string apiVersion = "v1") =>
        new() { Id = id, Title = title, ApiVersion = apiVersion, OrgKind = kind, Parent = parent ?? string.Empty };

    private static Scope Scp(
        string id, string organisation, string standard, string disposition = "In", string title = "T", string apiVersion = "v1") =>
        new()
        {
            Id = id,
            Title = title,
            ApiVersion = apiVersion,
            Organisation = organisation,
            Standard = standard,
            Disposition = disposition,
        };

    private static RequirementScope Rqs(
        string id, string organisation, string requirement, string disposition = "Out", string title = "T", string apiVersion = "v1") =>
        new()
        {
            Id = id,
            Title = title,
            ApiVersion = apiVersion,
            Organisation = organisation,
            Requirement = requirement,
            Disposition = disposition,
        };

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
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

        foreach (var t in new[]
                 {
                     "standards", "requirements", "controls", "organisations", "scopes",
                     "requirement_scopes", "control_requirements", "schema_migrations",
                 })
        {
            Assert.Contains(t, tables);
        }

        // The old scope->controls relation is dropped by the organisation migration; the
        // control->standard join is repointed to control_requirements by migration 008.
        Assert.DoesNotContain("scope_controls", tables);
        Assert.DoesNotContain("control_standards", tables);

        // id columns are binary-collated.
        var collation = await conn.ExecuteScalarAsync<string>(
            "SELECT collation_name FROM information_schema.columns "
            + "WHERE table_schema = DATABASE() AND table_name = 'standards' AND column_name = 'id';");
        Assert.Equal("utf8mb4_bin", collation);

        // requirements id and standard_id, and the control_requirements join columns, are binary-collated.
        foreach (var (table, column) in new[]
                 {
                     ("requirements", "id"), ("requirements", "standard_id"),
                     ("control_requirements", "control_id"), ("control_requirements", "requirement_id"),
                 })
        {
            var col = await conn.ExecuteScalarAsync<string>(
                "SELECT collation_name FROM information_schema.columns "
                + "WHERE table_schema = DATABASE() AND table_name = @Table AND column_name = @Column;",
                new { Table = table, Column = column });
            Assert.Equal("utf8mb4_bin", col);
        }

        // The four new nullable standards metadata columns exist.
        foreach (var column in new[] { "version", "authority", "publisher", "source_url" })
        {
            var exists = await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM information_schema.columns "
                + "WHERE table_schema = DATABASE() AND table_name = 'standards' AND column_name = @Column;",
                new { Column = column });
            Assert.Equal(1, exists);
        }

        // FK present on the maps_to join table (control and requirement FKs).
        var fkCount = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.table_constraints "
            + "WHERE table_schema = DATABASE() AND table_name = 'control_requirements' AND constraint_type = 'FOREIGN KEY';");
        Assert.True(fkCount >= 2);

        // control_requirements FKs reference controls and requirements.
        var joinFkTargets = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.key_column_usage "
            + "WHERE table_schema = DATABASE() AND table_name = 'control_requirements' "
            + "AND referenced_table_name IN ('controls', 'requirements');");
        Assert.Equal(2, joinFkTargets);

        // requirements FK to standards plus the standard_id index.
        var reqFk = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.key_column_usage "
            + "WHERE table_schema = DATABASE() AND table_name = 'requirements' "
            + "AND column_name = 'standard_id' AND referenced_table_name = 'standards';");
        Assert.Equal(1, reqFk);
        var reqIndex = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.statistics "
            + "WHERE table_schema = DATABASE() AND table_name = 'requirements' AND index_name = 'ix_requirements_standard_id';");
        Assert.True(reqIndex >= 1);

        // Organisation self-FK on parent_id.
        var orgSelfFk = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.key_column_usage "
            + "WHERE table_schema = DATABASE() AND table_name = 'organisations' "
            + "AND column_name = 'parent_id' AND referenced_table_name = 'organisations';");
        Assert.Equal(1, orgSelfFk);

        // Scope organisation/standard FKs.
        var scopeFks = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.key_column_usage "
            + "WHERE table_schema = DATABASE() AND table_name = 'scopes' "
            + "AND referenced_table_name IN ('organisations', 'standards');");
        Assert.Equal(2, scopeFks);

        // Unique key on (organisation_id, standard_id).
        var uniqueKeyCols = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.statistics "
            + "WHERE table_schema = DATABASE() AND table_name = 'scopes' "
            + "AND index_name = 'uq_scopes_organisation_standard' AND non_unique = 0;");
        Assert.Equal(2, uniqueKeyCols);

        // requirement_scopes id/organisation_id/requirement_id are binary-collated.
        foreach (var column in new[] { "id", "organisation_id", "requirement_id" })
        {
            var col = await conn.ExecuteScalarAsync<string>(
                "SELECT collation_name FROM information_schema.columns "
                + "WHERE table_schema = DATABASE() AND table_name = 'requirement_scopes' AND column_name = @Column;",
                new { Column = column });
            Assert.Equal("utf8mb4_bin", col);
        }

        // requirement_scopes FKs to organisations and requirements.
        var requirementScopeFks = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.key_column_usage "
            + "WHERE table_schema = DATABASE() AND table_name = 'requirement_scopes' "
            + "AND referenced_table_name IN ('organisations', 'requirements');");
        Assert.Equal(2, requirementScopeFks);

        // Unique key on (organisation_id, requirement_id) and the requirement_id index.
        var requirementScopeUnique = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.statistics "
            + "WHERE table_schema = DATABASE() AND table_name = 'requirement_scopes' "
            + "AND index_name = 'uq_requirement_scopes_organisation_requirement' AND non_unique = 0;");
        Assert.Equal(2, requirementScopeUnique);
        var requirementScopeIndex = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.statistics "
            + "WHERE table_schema = DATABASE() AND table_name = 'requirement_scopes' "
            + "AND index_name = 'ix_requirement_scopes_requirement_id';");
        Assert.True(requirementScopeIndex >= 1);

        // Both requirement_scopes FKs are ON DELETE RESTRICT (a referenced organisation or
        // requirement cannot be dropped while a requirement-scope still binds it).
        var requirementScopeDeleteRules = (await conn.QueryAsync<string>(
            "SELECT delete_rule FROM information_schema.referential_constraints "
            + "WHERE constraint_schema = DATABASE() AND table_name = 'requirement_scopes';"))
            .ToArray();
        Assert.Equal(2, requirementScopeDeleteRules.Length);
        Assert.All(requirementScopeDeleteRules, rule => Assert.Equal("RESTRICT", rule));

        // The unique key spans (organisation_id, requirement_id) in that column order.
        var requirementScopeUniqueColumns = (await conn.QueryAsync<string>(
            "SELECT column_name FROM information_schema.statistics "
            + "WHERE table_schema = DATABASE() AND table_name = 'requirement_scopes' "
            + "AND index_name = 'uq_requirement_scopes_organisation_requirement' "
            + "ORDER BY seq_in_index;"))
            .ToArray();
        Assert.Equal(["organisation_id", "requirement_id"], requirementScopeUniqueColumns);

        // Two requirement-scope ids differing only in case stay distinct rows under utf8mb4_bin.
        // Each binds a distinct requirement so the (organisation, requirement) unique key is not hit.
        await conn.ExecuteAsync(
            "INSERT INTO standards (id, api_version, title, created_at, updated_at) VALUES ('s', 'v1', 'S', NOW(6), NOW(6));");
        await conn.ExecuteAsync(
            "INSERT INTO requirements (id, api_version, title, standard_id, theme, statement, citation_label, citation_url, created_at, updated_at) "
            + "VALUES ('r', 'v1', 'R', 's', 'T', 'S', 'L', 'https://example.com/r', NOW(6), NOW(6)), "
            + "('R', 'v1', 'R', 's', 'T', 'S', 'L', 'https://example.com/R', NOW(6), NOW(6));");
        await conn.ExecuteAsync(
            "INSERT INTO organisations (id, api_version, title, kind, created_at, updated_at) VALUES ('o', 'v1', 'O', 'Company', NOW(6), NOW(6));");
        await conn.ExecuteAsync(
            "INSERT INTO requirement_scopes (id, api_version, title, organisation_id, requirement_id, disposition, created_at, updated_at) "
            + "VALUES ('rs-a', 'v1', 'T', 'o', 'r', 'Out', NOW(6), NOW(6)), ('RS-A', 'v1', 'T', 'o', 'R', 'Out', NOW(6), NOW(6));");
        var distinctRows = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM requirement_scopes WHERE id IN ('rs-a', 'RS-A');");
        Assert.Equal(2, distinctRows);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
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

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
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

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
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

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
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

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task SyncRoundTripsCountsAndCrossRefs()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        var importer = new MySqlGitOpsImporter(db.ConnectionFactory);
        var store = new MySqlComplianceStore(db.ConnectionFactory);

        await importer.ImportAsync(Config(
            [Std("std-a", version: "3.3", authority: "NCSC"), Std("std-b")],
            [Ctrl("ctrl-a", ["req-a", "req-b"])],
            [Org("org-a"), Org("org-eng", "Department", "org-a")],
            [Scp("scope-a", "org-a", "std-a")],
            [Req("req-a", "std-a"), Req("req-b", "std-b")],
            [Rqs("rs-a", "org-a", "req-a")]));

        var counts = await store.GetCountsAsync();
        Assert.Equal(new ComplianceCounts(2, 1, 2, 2, 1, 1), counts);

        var requirementScope = Assert.Single(await store.GetRequirementScopesAsync());
        Assert.Equal("rs-a", requirementScope.Id);
        Assert.Equal("org-a", requirementScope.Organisation);
        Assert.Equal("req-a", requirementScope.Requirement);
        Assert.Equal("Out", requirementScope.Disposition);

        var standard = (await store.GetStandardsAsync()).Single(s => s.Id == "std-a");
        Assert.Equal("3.3", standard.Version);
        Assert.Equal("NCSC", standard.Authority);

        var requirements = await store.GetRequirementsAsync();
        Assert.Equal(["req-a", "req-b"], requirements.Select(r => r.Id).ToArray());
        Assert.Equal("std-a", requirements.Single(r => r.Id == "req-a").Standard);

        var control = Assert.Single(await store.GetControlsAsync());
        Assert.Equal(["req-a", "req-b"], control.MapsTo);

        var organisations = await store.GetOrganisationsAsync();
        Assert.Equal(["org-a", "org-eng"], organisations.Select(o => o.Id).ToArray());
        var child = organisations.Single(o => o.Id == "org-eng");
        Assert.Equal("Department", child.Kind);
        Assert.Equal("org-a", child.Parent);
        Assert.Null(organisations.Single(o => o.Id == "org-a").Parent);

        var scope = Assert.Single(await store.GetScopesAsync());
        Assert.Equal("org-a", scope.Organisation);
        Assert.Equal("std-a", scope.Standard);
        Assert.Equal("In", scope.Disposition);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task DuplicateScopeMappingViolatesUniqueKey()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var importer = new MySqlGitOpsImporter(db.ConnectionFactory);

        await importer.ImportAsync(Config(
            [Std("std-a")], [], [Org("org-a")], [Scp("scope-a", "org-a", "std-a")]));

        // A second row for the same (organisation, standard) pair, written directly, is rejected.
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await Assert.ThrowsAsync<MySqlException>(() => conn.ExecuteAsync(
            "INSERT INTO scopes (id, api_version, title, organisation_id, standard_id, disposition, created_at, updated_at) "
            + "VALUES ('scope-b', 'v1', 'T', 'org-a', 'std-a', 'Out', NOW(6), NOW(6));"));
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task SameOrganisationAcrossStandardsIsAllowed()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var importer = new MySqlGitOpsImporter(db.ConnectionFactory);
        var store = new MySqlComplianceStore(db.ConnectionFactory);

        await importer.ImportAsync(Config(
            [Std("std-a"), Std("std-b")],
            [],
            [Org("org-a")],
            [Scp("scope-a", "org-a", "std-a"), Scp("scope-b", "org-a", "std-b", "Out")]));

        Assert.Equal(2, (await store.GetScopesAsync()).Count);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task ResyncRenamedScopeKeepingSamePairSurvives()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var importer = new MySqlGitOpsImporter(db.ConnectionFactory);
        var store = new MySqlComplianceStore(db.ConnectionFactory);

        await importer.ImportAsync(Config(
            [Std("std-a")], [], [Org("org-a")],
            [Scp("scope-old", "org-a", "std-a", "Out")]));

        // Rename the scope id while keeping the same (organisation, standard) pair. The unique key
        // means the new id must replace the old row without dropping the pair's disposition.
        await importer.ImportAsync(Config(
            [Std("std-a")], [], [Org("org-a")],
            [Scp("scope-new", "org-a", "std-a", "Out")]));

        var scope = Assert.Single(await store.GetScopesAsync());
        Assert.Equal("scope-new", scope.Id);
        Assert.Equal("org-a", scope.Organisation);
        Assert.Equal("std-a", scope.Standard);
        Assert.Equal("Out", scope.Disposition);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task ResyncRemovesDroppedOrganisationChildBeforeParent()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var importer = new MySqlGitOpsImporter(db.ConnectionFactory);
        var store = new MySqlComplianceStore(db.ConnectionFactory);

        await importer.ImportAsync(Config(
            [], [], [Org("root"), Org("child", "Department", "root")], []));

        // Drop both. The child references the parent via the self-FK, so the importer must delete
        // the child before the parent (ON DELETE RESTRICT would otherwise block it).
        await importer.ImportAsync(Config([], [], [], []));

        Assert.Empty(await store.GetOrganisationsAsync());
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
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

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
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

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task ResyncUpdatesStoredApiVersion()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var importer = new MySqlGitOpsImporter(db.ConnectionFactory);

        await importer.ImportAsync(Config([Std("std-a", apiVersion: "freeboard.dev/v1alpha1")], [], []));
        await importer.ImportAsync(Config([Std("std-a", apiVersion: "freeboard.dev/v1beta1")], [], []));

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var apiVersion = await conn.ExecuteScalarAsync<string>(
            "SELECT api_version FROM standards WHERE id = 'std-a';");
        Assert.Equal("freeboard.dev/v1beta1", apiVersion);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task FkSafeDropOfReferencedStandardSucceeds()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var importer = new MySqlGitOpsImporter(db.ConnectionFactory);
        var store = new MySqlComplianceStore(db.ConnectionFactory);

        // Old state: ctrl-a maps to req-a (std-a) and req-b (std-b).
        await importer.ImportAsync(Config(
            [Std("std-a"), Std("std-b")],
            [Ctrl("ctrl-a", ["req-a", "req-b"])],
            [],
            requirements: [Req("req-a", "std-a"), Req("req-b", "std-b")]));

        // New config drops std-b (and its requirement) and re-maps ctrl-a to req-a only. Must not FK-violate.
        await importer.ImportAsync(Config(
            [Std("std-a")],
            [Ctrl("ctrl-a", ["req-a"])],
            [],
            requirements: [Req("req-a", "std-a")]));

        Assert.Single(await store.GetStandardsAsync());
        Assert.Equal(["req-a"], Assert.Single(await store.GetControlsAsync()).MapsTo);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task FkSafeDropOfStandardReferencedByScopeSucceeds()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var importer = new MySqlGitOpsImporter(db.ConnectionFactory);
        var store = new MySqlComplianceStore(db.ConnectionFactory);

        // Old state: scope-a maps org-a to std-b (a scopes.standard_id FK to standards).
        await importer.ImportAsync(Config(
            [Std("std-a"), Std("std-b")],
            [],
            [Org("org-a")],
            [Scp("scope-a", "org-a", "std-b")]));

        // New config drops std-b and the scope that referenced it. The importer must delete the
        // scope before the standard so the standard drop does not violate the FK.
        await importer.ImportAsync(Config(
            [Std("std-a")],
            [],
            [Org("org-a")]));

        Assert.Single(await store.GetStandardsAsync());
        Assert.Empty(await store.GetScopesAsync());
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task FkSafeDropOfStandardThatHadRequirementsSucceeds()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var importer = new MySqlGitOpsImporter(db.ConnectionFactory);
        var store = new MySqlComplianceStore(db.ConnectionFactory);

        // Old state: std-b owns req-b. requirements reference standards with ON DELETE RESTRICT.
        await importer.ImportAsync(Config(
            [Std("std-a"), Std("std-b")],
            [],
            [],
            requirements: [Req("req-a", "std-a"), Req("req-b", "std-b")]));

        // New config drops std-b and its requirement. The importer must delete the requirement before
        // the standard so the RESTRICT FK is not hit.
        await importer.ImportAsync(Config(
            [Std("std-a")],
            [],
            [],
            requirements: [Req("req-a", "std-a")]));

        Assert.Single(await store.GetStandardsAsync());
        Assert.Equal(["req-a"], (await store.GetRequirementsAsync()).Select(r => r.Id).ToArray());
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task ResyncRemovesOrganisationAndRequirementThatHadRequirementScope()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var importer = new MySqlGitOpsImporter(db.ConnectionFactory);
        var store = new MySqlComplianceStore(db.ConnectionFactory);

        // Old state: rs-a binds org-gone to req-gone (a requirement of std-a). Both FKs RESTRICT.
        await importer.ImportAsync(Config(
            [Std("std-a")],
            [],
            [Org("org-keep"), Org("org-gone")],
            [],
            [Req("req-keep", "std-a"), Req("req-gone", "std-a")],
            [Rqs("rs-a", "org-gone", "req-gone")]));

        // New config drops the organisation and the requirement (and the requirement-scope). The
        // importer must replace the requirement-scope set before the absent-organisation and
        // absent-requirement deletes, so neither RESTRICT FK is hit.
        await importer.ImportAsync(Config(
            [Std("std-a")],
            [],
            [Org("org-keep")],
            [],
            [Req("req-keep", "std-a")]));

        Assert.Empty(await store.GetRequirementScopesAsync());
        Assert.Equal(["org-keep"], (await store.GetOrganisationsAsync()).Select(o => o.Id).ToArray());
        Assert.Equal(["req-keep"], (await store.GetRequirementsAsync()).Select(r => r.Id).ToArray());
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task ResyncRenamedRequirementScopeKeepingSamePairSurvives()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var importer = new MySqlGitOpsImporter(db.ConnectionFactory);
        var store = new MySqlComplianceStore(db.ConnectionFactory);

        await importer.ImportAsync(Config(
            [Std("std-a")], [], [Org("org-a")], [],
            [Req("req-a", "std-a")],
            [Rqs("rs-old", "org-a", "req-a", "Out")]));

        // Rename the requirement-scope id while keeping the same (organisation, requirement) pair.
        // The whole-set replace drops the old row and inserts the new id, so no unique-key collision.
        await importer.ImportAsync(Config(
            [Std("std-a")], [], [Org("org-a")], [],
            [Req("req-a", "std-a")],
            [Rqs("rs-new", "org-a", "req-a", "Out")]));

        var requirementScope = Assert.Single(await store.GetRequirementScopesAsync());
        Assert.Equal("rs-new", requirementScope.Id);
        Assert.Equal("org-a", requirementScope.Organisation);
        Assert.Equal("req-a", requirementScope.Requirement);
        Assert.Equal("Out", requirementScope.Disposition);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task ResyncSwappingRequirementScopePairsKeepingIdsSurvives()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var importer = new MySqlGitOpsImporter(db.ConnectionFactory);
        var store = new MySqlComplianceStore(db.ConnectionFactory);

        await importer.ImportAsync(Config(
            [Std("std-a")], [], [Org("org-a"), Org("org-b")], [],
            [Req("req-x", "std-a"), Req("req-y", "std-a")],
            [Rqs("rs-1", "org-a", "req-x", "Out"), Rqs("rs-2", "org-b", "req-y", "In")]));

        // Two requirement-scopes exchange their (organisation, requirement) pairs while keeping
        // their ids. Neither id is absent, so a prune-then-upsert would not free the pairs and the
        // upsert could match the unique pair key instead of the primary key, corrupting the result.
        // The whole-set replace re-inserts both correctly.
        await importer.ImportAsync(Config(
            [Std("std-a")], [], [Org("org-a"), Org("org-b")], [],
            [Req("req-x", "std-a"), Req("req-y", "std-a")],
            [Rqs("rs-1", "org-b", "req-y", "Out"), Rqs("rs-2", "org-a", "req-x", "In")]));

        var scopes = (await store.GetRequirementScopesAsync()).ToDictionary(r => r.Id);
        Assert.Equal(2, scopes.Count);
        Assert.Equal(("org-b", "req-y", "Out"), (scopes["rs-1"].Organisation, scopes["rs-1"].Requirement, scopes["rs-1"].Disposition));
        Assert.Equal(("org-a", "req-x", "In"), (scopes["rs-2"].Organisation, scopes["rs-2"].Requirement, scopes["rs-2"].Disposition));
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task WriteStoreUpsertsRequirementScopeAndRejectsInvalidWrites()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var importer = new MySqlGitOpsImporter(db.ConnectionFactory);
        var store = new MySqlComplianceStore(db.ConnectionFactory);
        var writeStore = new MySqlComplianceWriteStore(db.ConnectionFactory);

        await importer.ImportAsync(Config(
            [Std("std-a")], [], [Org("org-a")], [], [Req("req-a", "std-a")]));

        // A valid app-managed upsert persists and reads back through the read store.
        Assert.True((await writeStore.UpsertRequirementScopeDispositionAsync("rs-a", "T", "org-a", "req-a", "Out")).Ok);
        var persisted = Assert.Single(await store.GetRequirementScopesAsync());
        Assert.Equal("rs-a", persisted.Id);
        Assert.Equal("org-a", persisted.Organisation);
        Assert.Equal("req-a", persisted.Requirement);
        Assert.Equal("Out", persisted.Disposition);

        // Each invariant violation returns WriteResult.Fail (the 422-mapped failure) and writes nothing.
        Assert.False((await writeStore.UpsertRequirementScopeDispositionAsync("rs-b", "T", "absent-org", "req-a", "Out")).Ok);
        Assert.False((await writeStore.UpsertRequirementScopeDispositionAsync("rs-b", "T", "org-a", "absent-req", "Out")).Ok);
        Assert.False((await writeStore.UpsertRequirementScopeDispositionAsync("rs-b", "T", "org-a", "req-a", "Sideways")).Ok);
        Assert.Single(await store.GetRequirementScopesAsync());
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task WriteStoreCreatePathConflictsWithoutOverwritingExistingRow()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var importer = new MySqlGitOpsImporter(db.ConnectionFactory);
        var store = new MySqlComplianceStore(db.ConnectionFactory);
        var writeStore = new MySqlComplianceWriteStore(db.ConnectionFactory);

        await importer.ImportAsync(Config([Std("std-a")], [], [Org("org-a")], [], []));

        // Organisation create is INSERT-only: a second create for an id that already exists conflicts
        // (409-mapped) instead of silently last-write-wins overwriting the stored row. expectExisting
        // defaults to false, which is the create path.
        Assert.True((await writeStore.UpsertOrganisationAsync("org-x", "First", "Company", null)).Ok);
        Assert.True((await writeStore.UpsertOrganisationAsync("org-x", "Second", "Company", null)).IsConflict);
        Assert.Equal("First", (await store.GetOrganisationsAsync()).Single(o => o.Id == "org-x").Title);

        // Scope create is INSERT-only too (expectedCurrentOrganisation null is the create path).
        Assert.True((await writeStore.UpsertScopeDispositionAsync("sc-x", "First", "org-a", "std-a", "In")).Ok);
        Assert.True((await writeStore.UpsertScopeDispositionAsync("sc-x", "Second", "org-a", "std-a", "In")).IsConflict);
        Assert.Equal("First", (await store.GetScopesAsync()).Single(s => s.Id == "sc-x").Title);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task GetStatementOfApplicabilityInputsMatchesIndividualReads()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var importer = new MySqlGitOpsImporter(db.ConnectionFactory);
        var store = new MySqlComplianceStore(db.ConnectionFactory);

        await importer.ImportAsync(Config(
            [Std("std-a")],
            [],
            [Org("org-a"), Org("org-eng", "Department", "org-a")],
            [Scp("scope-a", "org-a", "std-a")],
            [Req("req-a", "std-a"), Req("req-b", "std-a")],
            [Rqs("rs-a", "org-a", "req-a")]));

        var inputs = await store.GetStatementOfApplicabilityInputsAsync();

        Assert.Equal(
            (await store.GetOrganisationsAsync()).Select(o => o.Id).ToArray(),
            inputs.Organisations.Select(o => o.Id).ToArray());
        Assert.Equal(
            (await store.GetScopesAsync()).Select(s => s.Id).ToArray(),
            inputs.Scopes.Select(s => s.Id).ToArray());
        Assert.Equal(
            (await store.GetRequirementsAsync()).Select(r => r.Id).ToArray(),
            inputs.Requirements.Select(r => r.Id).ToArray());
        Assert.Equal(
            (await store.GetRequirementScopesAsync()).Select(r => r.Id).ToArray(),
            inputs.RequirementScopes.Select(r => r.Id).ToArray());
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task CaseDistinctRequirementIdsRemainDistinctRows()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var importer = new MySqlGitOpsImporter(db.ConnectionFactory);
        var store = new MySqlComplianceStore(db.ConnectionFactory);

        await importer.ImportAsync(Config(
            [Std("std-a")],
            [],
            [],
            requirements: [Req("req-a", "std-a"), Req("REQ-A", "std-a")]));

        Assert.Equal(2, (await store.GetRequirementsAsync()).Count);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
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
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
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
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
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
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
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
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
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
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
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

        // Send a sudo magic-link for user A; its token lands in its own row, and verify-and-consume
        // (the sudo-only verify) matches it. FindOrCreate makes its own dedupe-keyed challenge row.
        var now = DateTime.UtcNow;
        var link = hasher.MintPrefixless();
        var send = await challenges.FindOrCreateSudoMagicLinkAsync(
            userA.Id, 1, link.Hash, link.KeyVersion, now.AddMinutes(10), now.AddMinutes(10), 3, now);
        Assert.True(send.Sent);

        // User B cannot consume user A's challenge.
        Assert.False(await challenges.VerifyAndConsumeMagicLinkAsync(send.ChallengeId, userB.Id, link.Token, now));

        // User A consumes it once...
        Assert.True(await challenges.VerifyAndConsumeMagicLinkAsync(send.ChallengeId, userA.Id, link.Token, now));
        // ...and a replay fails (single-use).
        Assert.False(await challenges.VerifyAndConsumeMagicLinkAsync(send.ChallengeId, userA.Id, link.Token, now));
    }

    // Concurrent sudo magic-link sends with NO pre-existing challenge must converge on ONE row
    // (the (user_id, sudo_dedupe_key) unique key + INSERT ... ON DUPLICATE KEY UPDATE), so the
    // per-challenge re-send cap holds instead of being multiplied by the race.
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
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

        // The cap holds atomically: concurrent sends serialise on the challenge row, so EXACTLY
        // maxSends are accepted and each lands its OWN token row - the race neither multiplies the
        // cap nor clobbers an accepted token (every accepted link stays verifiable).
        var accepted = results.Count(r => r.Sent);
        Assert.Equal(maxSends, accepted);
        Assert.Single(results.Select(r => r.ChallengeId).Distinct());

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var challengeCount = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM mfa_login_challenges WHERE user_id = @UserId AND sudo_dedupe_key = 'magic_link';",
            new { UserId = user.Id });
        Assert.Equal(1, challengeCount);
        var activeTokens = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM mfa_sudo_magic_link_tokens t "
            + "JOIN mfa_login_challenges c ON c.id = t.challenge_id "
            + "WHERE c.user_id = @UserId AND c.sudo_dedupe_key = 'magic_link' "
            + "AND t.consumed_at IS NULL AND t.expires_at > @Now;",
            new { UserId = user.Id, Now = now });
        Assert.Equal((long)maxSends, activeTokens);
    }

    // Option D: each sudo magic-link send stores its own token, so a later send does NOT clobber an
    // earlier emitted token. Either emitted token verifies; consuming the challenge via one makes the
    // step-up single-use. The re-send cap counts active tokens.
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task SudoMagicLinkSendsAreEachIndependentlyVerifiable()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        var ulids = new Freeboard.Persistence.Auth.UlidFactory();
        var users = new Freeboard.Persistence.Auth.MySqlUserStore(db.ConnectionFactory, ulids);
        var hasher = new Freeboard.Persistence.Auth.HmacTokenHasher(TestCrypto());
        var challenges = new Freeboard.Persistence.Auth.MySqlMfaChallengeStore(db.ConnectionFactory, ulids, hasher);
        var now = DateTime.UtcNow;
        var exp = now.AddMinutes(10);

        // The EARLIER token still verifies after a later send (the fix).
        var u1 = await users.CreateAsync(new Freeboard.Persistence.Auth.NewUser("e1@e.com", "E1", "member"));
        var a = hasher.MintPrefixless();
        var sendA = await challenges.FindOrCreateSudoMagicLinkAsync(u1.Id, 1, a.Hash, a.KeyVersion, exp, exp, 3, now);
        var b = hasher.MintPrefixless();
        var sendB = await challenges.FindOrCreateSudoMagicLinkAsync(u1.Id, 1, b.Hash, b.KeyVersion, exp, exp, 3, now);
        Assert.True(sendA.Sent);
        Assert.True(sendB.Sent);
        Assert.Equal(sendA.ChallengeId, sendB.ChallengeId);
        Assert.True(await challenges.VerifyAndConsumeMagicLinkAsync(sendA.ChallengeId, u1.Id, a.Token, now));
        // Single-use: once the challenge is consumed, the other token cannot be used.
        Assert.False(await challenges.VerifyAndConsumeMagicLinkAsync(sendB.ChallengeId, u1.Id, b.Token, now));

        // The LATER token is itself valid too (either of the two works, not just one).
        var u2 = await users.CreateAsync(new Freeboard.Persistence.Auth.NewUser("e2@e.com", "E2", "member"));
        var c = hasher.MintPrefixless();
        var sendC = await challenges.FindOrCreateSudoMagicLinkAsync(u2.Id, 1, c.Hash, c.KeyVersion, exp, exp, 3, now);
        var d = hasher.MintPrefixless();
        await challenges.FindOrCreateSudoMagicLinkAsync(u2.Id, 1, d.Hash, d.KeyVersion, exp, exp, 3, now);
        Assert.True(await challenges.VerifyAndConsumeMagicLinkAsync(sendC.ChallengeId, u2.Id, d.Token, now));

        // Cap: a third active send is rejected when maxSends = 2.
        var u3 = await users.CreateAsync(new Freeboard.Persistence.Auth.NewUser("e3@e.com", "E3", "member"));
        var m1 = hasher.MintPrefixless();
        var m2 = hasher.MintPrefixless();
        var m3 = hasher.MintPrefixless();
        Assert.True((await challenges.FindOrCreateSudoMagicLinkAsync(u3.Id, 1, m1.Hash, m1.KeyVersion, exp, exp, 2, now)).Sent);
        Assert.True((await challenges.FindOrCreateSudoMagicLinkAsync(u3.Id, 1, m2.Hash, m2.KeyVersion, exp, exp, 2, now)).Sent);
        Assert.False((await challenges.FindOrCreateSudoMagicLinkAsync(u3.Id, 1, m3.Hash, m3.KeyVersion, exp, exp, 2, now)).Sent);
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
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
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
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
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
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
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
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
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
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
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
