using Dapper;
using Freeboard.Persistence.System;
using Freeboard.TestInfrastructure;
using MySqlConnector;

namespace Freeboard.Persistence.Tests;

/// <summary>
/// Integration tests for migration 021, which merges evidence_collectors and attestation_templates into
/// one collectors table, against a real MySQL discovered via FREEBOARD_TEST_DB. Each test SKIPS cleanly
/// (not fails) when the env var is absent and gets a fresh throwaway database.
///
/// The pattern is the same throughout: apply every migration BELOW 021, seed pre-merge rows directly,
/// then apply 021 and assert what it copied, what it refused, and what it left behind. Refusals assert
/// the named check constraint and that nothing was lost; the id collision asserts its OUTCOME only,
/// because it fails on a key name rather than a rule name and that text is not stable across servers.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class CollectorMergeMigrationTests
{
    private const int MergeOrdinal = 21;

    private static async Task<MySqlTestDatabase> RequireDbAsync()
    {
        var db = await MySqlTestDatabase.TryCreateAsync();
        Skip.If(db is null, $"{MySqlTestDatabase.EnvVar} not set; skipping MySQL integration test.");
        return db!;
    }

    /// <summary>Applies every migration before 021, leaving the database in its pre-merge shape.</summary>
    private static async Task MigrateToPreMergeAsync(MySqlConnection conn)
    {
        foreach (var migration in MigrationCatalog.Load(typeof(IMigrationRunner).Assembly)
                     .Where(m => m.Ordinal < MergeOrdinal))
        {
            await conn.ExecuteAsync(migration.Sql);
        }
    }

    /// <summary>Applies 021 alone, so a failure surfaces as the exception the migration raised.</summary>
    private static async Task ApplyMergeAsync(MySqlConnection conn)
    {
        var merge = MigrationCatalog.Load(typeof(IMigrationRunner).Assembly).Single(m => m.Ordinal == MergeOrdinal);
        await conn.ExecuteAsync(merge.Sql);
    }

    private static async Task<MySqlException> ApplyMergeExpectingFailureAsync(MySqlConnection conn) =>
        await Assert.ThrowsAsync<MySqlException>(() => ApplyMergeAsync(conn));

    private static async Task SeedControlAsync(MySqlConnection conn, string id = "ctrl-a") =>
        await conn.ExecuteAsync(
            "INSERT INTO controls (id, api_version, title, created_at, updated_at) "
            + "VALUES (@Id, 'v1', 'C', NOW(6), NOW(6));",
            new { Id = id });

    private static async Task SeedVendorAsync(MySqlConnection conn, string id = "vendor-a") =>
        await conn.ExecuteAsync(
            "INSERT INTO assets (id, type, source, api_version, title, created_at, updated_at) "
            + "VALUES (@Id, 'Vendor', 'declared', 'v1', 'V', NOW(6), NOW(6));",
            new { Id = id });

    private static async Task SeedConnectionAsync(MySqlConnection conn, string id = "fleet-prod") =>
        await conn.ExecuteAsync(
            "INSERT INTO integration_connections "
            + "(id, api_version, title, provider, discovery_cadence, base_url, vendor_id, created_at, updated_at) "
            + "VALUES (@Id, 'v1', 'Fleet', 'fleet', 'daily', 'https://fleet.example.com', NULL, NOW(6), NOW(6));",
            new { Id = id });

    /// <summary>Seeds one pre-merge evidence_collectors row. `checks` and `config` are raw JSON text.</summary>
    private static async Task SeedCollectorAsync(
        MySqlConnection conn,
        string id,
        string type,
        string? connectionId = null,
        string? checksJson = null,
        string? configJson = null,
        string? vendorId = null,
        string frequency = "daily",
        int? threshold = null) =>
        await conn.ExecuteAsync(
            "INSERT INTO evidence_collectors "
            + "(id, api_version, title, control_id, vendor_id, connection_id, type, frequency, threshold, "
            + "config, checks, created_at, updated_at) "
            + "VALUES (@Id, 'v1', 'T', 'ctrl-a', @VendorId, @ConnectionId, @Type, @Frequency, @Threshold, "
            + "@ConfigJson, @ChecksJson, NOW(6), NOW(6));",
            new { Id = id, Type = type, ConnectionId = connectionId, ChecksJson = checksJson, ConfigJson = configJson, VendorId = vendorId, Frequency = frequency, Threshold = threshold });

    private static async Task SeedTemplateAsync(
        MySqlConnection conn,
        string id,
        string type,
        string? body = null,
        string? fieldsJson = null,
        int? passMark = null,
        string? quizJson = null) =>
        await conn.ExecuteAsync(
            "INSERT INTO attestation_templates "
            + "(id, api_version, title, control_id, type, body, fields, pass_mark, quiz, created_at, updated_at) "
            + "VALUES (@Id, 'v1', 'T', 'ctrl-a', @Type, @Body, @FieldsJson, @PassMark, @QuizJson, NOW(6), NOW(6));",
            new { Id = id, Type = type, Body = body, FieldsJson = fieldsJson, PassMark = passMark, QuizJson = quizJson });

    private static async Task<bool> TableExistsAsync(MySqlConnection conn, string table) =>
        await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.tables "
            + "WHERE table_schema = DATABASE() AND table_name = @Table;",
            new { Table = table }) == 1;

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task MergeCopiesBothSourceTablesAndDropsThem()
    {
        await using var db = await RequireDbAsync();
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await MigrateToPreMergeAsync(conn);

        await SeedControlAsync(conn);
        await SeedVendorAsync(conn);
        await SeedConnectionAsync(conn);
        // The common integration shape: a NULL free-form config alongside a populated checks column.
        await SeedCollectorAsync(
            conn, "coll-int", "integration", connectionId: "fleet-prod", vendorId: "vendor-a", threshold: 90,
            checksJson: """[{"SourceKey":"12","Name":"mfa-enforced","Severity":"Hard"}]""");
        // Both NULL: must land with config SQL NULL, never {"Checks": null}.
        await SeedCollectorAsync(conn, "coll-script", "script");
        // A free-form config map whose keys the merged schema does not register: must NOT be carried.
        await SeedCollectorAsync(conn, "coll-agent", "agent", configJson: """{"endpoint":"policies.mfa"}""");
        // The retired attestation type tokens re-tokenize.
        await SeedCollectorAsync(conn, "coll-manual", "manual-attestation", frequency: "annual");
        await SeedTemplateAsync(
            conn, "tmpl-training", "training", body: "Read this.", passMark: 80,
            quizJson: """[{"Id":"q1","Prompt":"P","Options":["a","b"],"Answer":"a"}]""");
        // Every optional column NULL: must land with config SQL NULL, not {} and not JSON-null members.
        await SeedTemplateAsync(conn, "tmpl-manual", "manual");

        await ApplyMergeAsync(conn);

        var rows = (await conn.QueryAsync<(string Id, string Type, string? Provider, string? Connection, string? Vendor, string Frequency, int? Threshold, string? Config)>(
            "SELECT id AS Id, type AS Type, provider AS Provider, connection_id AS Connection, "
            + "vendor_id AS Vendor, frequency AS Frequency, threshold AS Threshold, config AS Config "
            + "FROM collectors ORDER BY id;")).ToDictionary(r => r.Id);
        Assert.Equal(6, rows.Count);

        var integration = rows["coll-int"];
        Assert.Equal("integration", integration.Type);
        Assert.Equal("fleet", integration.Provider); // derived from the joined connection
        Assert.Equal("fleet-prod", integration.Connection); // carried verbatim, not dropped by a shorthand
        Assert.Equal("vendor-a", integration.Vendor);
        Assert.Equal(90, integration.Threshold);
        Assert.Equal("12", await conn.ExecuteScalarAsync<string>(
            "SELECT config->>'$.Checks[0].SourceKey' FROM collectors WHERE id = 'coll-int';"));

        Assert.Null(rows["coll-script"].Config);
        Assert.Null(rows["coll-script"].Provider);
        // The old free-form config map is not carried: the merged column holds only schema-owned data.
        Assert.Null(rows["coll-agent"].Config);
        Assert.Equal("manual", rows["coll-manual"].Type); // manual-attestation -> manual
        Assert.Equal("annual", rows["coll-manual"].Frequency); // its own authored cadence, not the placeholder

        var training = rows["tmpl-training"];
        Assert.Equal("training", training.Type);
        Assert.Equal("annual", training.Frequency); // the documented placeholder
        Assert.Null(training.Vendor);
        Assert.Null(training.Provider);
        // pass_mark stays the INT the source column held, so it stores as a JSON number. Assert the
        // JSON_TYPE, not the unquoted value: `->>` reads 80 and "80" identically and could not tell
        // a number from a string.
        Assert.Equal(
            ("INTEGER", "80"),
            await conn.QuerySingleAsync<(string Type, string Value)>(
                "SELECT JSON_TYPE(config->'$.PassMark') AS Type, config->>'$.PassMark' AS Value "
                + "FROM collectors WHERE id = 'tmpl-training';"));
        Assert.Contains("Read this.", training.Config);
        Assert.Contains("Answer", training.Config);
        // The template carried no fields, and JSON_MERGE_PATCH exists precisely so an absent source
        // column leaves the member out rather than writing a JSON null.
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>(
            "SELECT JSON_CONTAINS_PATH(config, 'one', '$.Fields') FROM collectors WHERE id = 'tmpl-training';"));

        // An all-NULL template lands with config SQL NULL, not with an empty JSON object.
        Assert.Null(rows["tmpl-manual"].Config);

        // Both legacy tables are gone and neither scalar collector_id column gained a foreign key.
        Assert.False(await TableExistsAsync(conn, "evidence_collectors"));
        Assert.False(await TableExistsAsync(conn, "attestation_templates"));
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.key_column_usage "
            + "WHERE table_schema = DATABASE() AND table_name IN ('collector_scheduler_state', 'evidence_runs') "
            + "AND referenced_table_name IS NOT NULL;"));
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task CredentialForeignKeyIsRepointedBeforeTheLegacyDrop()
    {
        await using var db = await RequireDbAsync();
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await MigrateToPreMergeAsync(conn);

        await SeedControlAsync(conn);
        await SeedCollectorAsync(conn, "coll-script", "script");
        await conn.ExecuteAsync(
            "INSERT INTO collector_credentials (id, collector_id, token_hash, token_key_version, created_at) "
            + "VALUES ('01HZZZZZZZZZZZZZZZZZZZZZZZ', 'coll-script', UNHEX(REPEAT('AB', 32)), 1, NOW(6));");

        await ApplyMergeAsync(conn);

        // The credential survived the legacy-table drop and now references collectors.
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM collector_credentials;"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.key_column_usage "
            + "WHERE table_schema = DATABASE() AND table_name = 'collector_credentials' "
            + "AND referenced_table_name = 'collectors';"));

        // The cascade still fires from the merged table.
        await conn.ExecuteAsync("DELETE FROM collectors WHERE id = 'coll-script';");
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM collector_credentials;"));
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task IntegrationRowWithChecksButNoConnectionFailsOnTheProviderGuard()
    {
        await using var db = await RequireDbAsync();
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await MigrateToPreMergeAsync(conn);

        await SeedControlAsync(conn);
        // The checks MUST be seeded: without them the row trips the source guard first and this test
        // would assert the wrong constraint.
        await SeedCollectorAsync(
            conn, "coll-int", "integration",
            checksJson: """[{"SourceKey":"12","Name":"mfa-enforced","Severity":"Hard"}]""");

        var ex = await ApplyMergeExpectingFailureAsync(conn);

        Assert.Contains("ck_collectors_integration_provider", ex.Message, StringComparison.Ordinal);
        // The guard fires on the first copy step, before either drop, so nothing is lost.
        Assert.True(await TableExistsAsync(conn, "evidence_collectors"));
        Assert.True(await TableExistsAsync(conn, "attestation_templates"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM evidence_collectors;"));
    }

    // The non-integration branch of the source guard: a type that may carry neither payload.
    [RequiresEnvVarTheory(EnvVar = MySqlTestDatabase.EnvVar)]
    [InlineData("manual-attestation", """[{"SourceKey":"12","Name":"n","Severity":"Hard"}]""", null)]
    [InlineData("script", null, "fleet-prod")]
    public async Task NonIntegrationRowCarryingChecksOrAConnectionFailsBeforeAnythingIsCreated(
        string type, string? checksJson, string? connectionId)
    {
        await using var db = await RequireDbAsync();
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await MigrateToPreMergeAsync(conn);

        await SeedControlAsync(conn);
        await SeedConnectionAsync(conn);
        await SeedCollectorAsync(conn, "coll-a", type, connectionId: connectionId, checksJson: checksJson);

        var ex = await ApplyMergeExpectingFailureAsync(conn);

        Assert.Contains("ck_evidence_collectors_premerge_payload", ex.Message, StringComparison.Ordinal);
        // This guard runs before the CREATE TABLE, so a re-run after the repair needs no cleanup.
        Assert.False(await TableExistsAsync(conn, "collectors"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM evidence_collectors;"));
        // And it leaves no constraint behind, so the file replays.
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.table_constraints "
            + "WHERE constraint_schema = DATABASE() AND constraint_name = 'ck_evidence_collectors_premerge_payload';"));
    }

    // The integration branch. The first row is the pre-018 shape (checks and connection_id both NULL),
    // which is refused HERE rather than by the provider constraint. The [1], [null], and wrong-typed
    // member rows are the three that make the read-boundary claim true: a scalar item does not bind to a
    // check at all, a JSON null item binds to a NULL list element, and a present member of an
    // incompatible JSON type throws where an UNMATCHED member is merely ignored.
    [RequiresEnvVarTheory(EnvVar = MySqlTestDatabase.EnvVar)]
    [InlineData(null)]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("""{"SourceKey":"12"}""")]
    [InlineData("[1]")]
    [InlineData("[null]")]
    [InlineData("""[{"SourceKey":123,"Name":"MFA","Severity":"Hard"}]""")]
    public async Task IntegrationRowWithAMalformedChecksFailsBeforeAnythingIsCreated(string? checksJson)
    {
        await using var db = await RequireDbAsync();
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await MigrateToPreMergeAsync(conn);

        await SeedControlAsync(conn);
        await SeedCollectorAsync(conn, "coll-int", "integration", checksJson: checksJson);

        var ex = await ApplyMergeExpectingFailureAsync(conn);

        Assert.Contains("ck_evidence_collectors_premerge_payload", ex.Message, StringComparison.Ordinal);
        Assert.False(await TableExistsAsync(conn, "collectors"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM evidence_collectors;"));
    }

    // What the source guard deliberately lets through: it types the three known members, it does not
    // require them, restrict their token set, or enforce their uniqueness.
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task IntegrationRowWithAnIncompleteCheckItemMigratesAndReadsBack()
    {
        await using var db = await RequireDbAsync();
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await MigrateToPreMergeAsync(conn);

        await SeedControlAsync(conn);
        await SeedConnectionAsync(conn);
        await SeedCollectorAsync(
            conn, "coll-int", "integration", connectionId: "fleet-prod", checksJson: """[{"nope":1}]""");

        await ApplyMergeAsync(conn);

        // Carried verbatim under the config Checks key, and it binds on read to a check with empty
        // members because an UNMATCHED property is ignored.
        var store = new MySqlComplianceStore(db.ConnectionFactory);
        var check = Assert.Single(Assert.Single(await store.GetCollectorsAsync()).Config.Checks);
        Assert.Equal((string.Empty, string.Empty, string.Empty), (check.SourceKey, check.Name, check.Severity));
    }

    // The attestation half of the same read-boundary rule, on the two columns the merge re-shapes.
    [RequiresEnvVarTheory(EnvVar = MySqlTestDatabase.EnvVar)]
    [InlineData("null", null)]
    [InlineData("""{"Id":"f1"}""", null)]
    [InlineData("[1]", null)]
    [InlineData("[null]", null)]
    [InlineData("""[{"Id":"f1","Label":123,"Type":"boolean"}]""", null)]
    [InlineData(null, "null")]
    [InlineData(null, "[null]")]
    [InlineData(null, """[{"Id":"q1","Prompt":"P","Options":"a"}]""")]
    public async Task TemplateRowWithAMalformedFormFailsBeforeAnythingIsCreated(string? fieldsJson, string? quizJson)
    {
        await using var db = await RequireDbAsync();
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await MigrateToPreMergeAsync(conn);

        await SeedControlAsync(conn);
        await SeedTemplateAsync(conn, "tmpl-a", "manual", fieldsJson: fieldsJson, quizJson: quizJson);

        var ex = await ApplyMergeExpectingFailureAsync(conn);

        Assert.Contains("ck_attestation_templates_premerge_payload", ex.Message, StringComparison.Ordinal);
        Assert.False(await TableExistsAsync(conn, "collectors"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM attestation_templates;"));
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task TemplateRowWithAnEmptyOrIncompleteFormMigratesAndReadsBack()
    {
        await using var db = await RequireDbAsync();
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await MigrateToPreMergeAsync(conn);

        await SeedControlAsync(conn);
        // Neither list is required by any registered schema, so the guard has no minimum length; and it
        // types the known members without requiring them.
        await SeedTemplateAsync(conn, "tmpl-a", "manual", fieldsJson: "[]", quizJson: """[{"nope":1}]""");

        await ApplyMergeAsync(conn);

        var store = new MySqlComplianceStore(db.ConnectionFactory);
        var config = Assert.Single(await store.GetCollectorsAsync()).Config;
        Assert.Empty(config.Fields);
        Assert.Equal(string.Empty, Assert.Single(config.Quiz).Id);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task ValidRowsPassBothGuardsAndLeaveNoConstraintBehind()
    {
        await using var db = await RequireDbAsync();
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await MigrateToPreMergeAsync(conn);

        await SeedControlAsync(conn);
        await SeedConnectionAsync(conn);
        await SeedCollectorAsync(conn, "coll-manual", "manual-attestation", frequency: "annual");
        await SeedCollectorAsync(
            conn, "coll-int", "integration", connectionId: "fleet-prod",
            checksJson: """[{"SourceKey":"12","Name":"mfa-enforced","Severity":"Hard"}]""");
        await SeedTemplateAsync(
            conn, "tmpl-a", "manual",
            fieldsJson: """[{"Id":"f1","Label":"L","Type":"boolean","Options":[]}]""");

        await ApplyMergeAsync(conn);

        Assert.Equal(3, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM collectors;"));
        // Neither transitional guard survives, on either outcome, so the file replays after a repair.
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.table_constraints "
            + "WHERE constraint_schema = DATABASE() AND constraint_name IN "
            + "('ck_evidence_collectors_premerge_payload', 'ck_attestation_templates_premerge_payload');"));
    }

    // The accepted transient: a pre-merge attestation authored as a collector-plus-template PAIR on one
    // control lands as TWO rows, because nothing in the source schema links the two. Pinned so a future
    // reader sees the split was decided, not missed; the required gitops sync collapses it.
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task AnAttestationPairLandsAsTwoRowsAndTheFormLessRowStillReads()
    {
        await using var db = await RequireDbAsync();
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await MigrateToPreMergeAsync(conn);

        await SeedControlAsync(conn);
        await SeedVendorAsync(conn);
        await SeedCollectorAsync(
            conn, "coll-training", "training-attestation", vendorId: "vendor-a", frequency: "quarterly");
        await SeedTemplateAsync(
            conn, "tmpl-training", "training", passMark: 80,
            quizJson: """[{"Id":"q1","Prompt":"P","Options":["a","b"],"Answer":"a"}]""");

        await ApplyMergeAsync(conn);

        var store = new MySqlComplianceStore(db.ConnectionFactory);
        var rows = (await store.GetCollectorsAsync()).ToDictionary(c => c.Id);
        Assert.Equal(2, rows.Count);
        Assert.All(rows.Values, r => Assert.Equal("training", r.Type));
        Assert.All(rows.Values, r => Assert.Equal("ctrl-a", r.Control));

        // The cadence-carrying half: its own authored frequency and vendor, and no form at all. It reads
        // back without error, which is the condition that makes copying it acceptable.
        var cadenceHalf = rows["coll-training"];
        Assert.Equal("quarterly", cadenceHalf.Frequency);
        Assert.Equal("vendor-a", cadenceHalf.Vendor);
        Assert.Null(cadenceHalf.Config.PassMark);
        Assert.Empty(cadenceHalf.Config.Quiz);

        // The form-carrying half: the placeholder cadence and no vendor.
        var formHalf = rows["tmpl-training"];
        Assert.Equal("annual", formHalf.Frequency);
        Assert.Null(formHalf.Vendor);
        Assert.Equal(80, formHalf.Config.PassMark);
        Assert.Single(formHalf.Config.Quiz);
    }

    // An id shared across the two source tables is an ABORT, not a refusal, and it is reachable from a
    // config that is entirely VALID pre-merge, because duplicate-id detection runs per kind and each
    // legacy table has its own primary key. Asserted by OUTCOME only: it fails on a key name
    // (collectors.PRIMARY), not a rule name, and that wording has already changed across MySQL majors.
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task AnIdSharedByBothSourceTablesAbortsAndLosesNothing()
    {
        await using var db = await RequireDbAsync();
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await MigrateToPreMergeAsync(conn);

        await SeedControlAsync(conn);
        await SeedCollectorAsync(conn, "shared-id", "script");
        await SeedTemplateAsync(conn, "shared-id", "manual");

        await Assert.ThrowsAnyAsync<Exception>(() => ApplyMergeAsync(conn));

        Assert.True(await TableExistsAsync(conn, "evidence_collectors"));
        Assert.True(await TableExistsAsync(conn, "attestation_templates"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM evidence_collectors;"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM attestation_templates;"));
    }
}
