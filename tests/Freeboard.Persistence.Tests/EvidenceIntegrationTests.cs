using Dapper;
using Freeboard.Persistence;
using Freeboard.Persistence.Auth;
using Freeboard.Persistence.System;
using Freeboard.TestInfrastructure;
using MySqlConnector;

namespace Freeboard.Persistence.Tests;

/// <summary>
/// Integration tests for the evidence schema (migration 011), the append-only store pair, and the
/// computed AssessmentResult, against a real MySQL discovered via FREEBOARD_TEST_DB. Each test SKIPS
/// cleanly when the env var is absent.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class EvidenceIntegrationTests
{
    private static async Task<MySqlTestDatabase> RequireDbAsync()
    {
        var db = await MySqlTestDatabase.TryCreateAsync();
        Skip.If(db is null, $"{MySqlTestDatabase.EnvVar} not set; skipping MySQL integration test.");
        return db!;
    }

    private static async Task MigrateAsync(MySqlTestDatabase db) =>
        await new MySqlMigrationRunner(db.ConnectionFactory, typeof(IMigrationRunner).Assembly).ApplyPendingAsync();

    private static NewEvidenceRun Run(
        string org,
        string requirement,
        string vendor,
        string collectorRef,
        string result = "Pass",
        DateTime? collectedAt = null,
        DateTime? receivedAt = null,
        string? rawPayload = null,
        params NewEvidenceCheck[] checks) =>
        new(org, requirement, vendor, collectorRef, result,
            collectedAt ?? DateTime.UtcNow, receivedAt, rawPayload, checks);

    private static NewEvidenceCheck Check(string name, string severity, string result, string? detail = null) =>
        new(name, severity, result, detail);

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task MigrationCreatesTablesUniqueKeyAndTriggers()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var tables = (await conn.QueryAsync<string>(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = DATABASE();"))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var t in new[] { "evidence_runs", "evidence_checks", "attestation_responses" })
        {
            Assert.Contains(t, tables);
        }

        // The (vendor, collector_ref) idempotency key exists and is unique.
        var uniqueCols = (await conn.QueryAsync<string>(
            "SELECT column_name FROM information_schema.statistics "
            + "WHERE table_schema = DATABASE() AND table_name = 'evidence_runs' "
            + "AND index_name = 'uq_evidence_runs_vendor_collector_ref' AND non_unique = 0 "
            + "ORDER BY seq_in_index;")).ToArray();
        Assert.Equal(["vendor", "collector_ref"], uniqueCols);

        // The six append-only triggers exist.
        var triggers = (await conn.QueryAsync<string>(
            "SELECT trigger_name FROM information_schema.triggers WHERE trigger_schema = DATABASE();"))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var trg in new[]
                 {
                     "trg_evidence_runs_no_update", "trg_evidence_runs_no_delete",
                     "trg_evidence_checks_no_update", "trg_evidence_checks_no_delete",
                     "trg_attestation_responses_no_update", "trg_attestation_responses_no_delete",
                 })
        {
            Assert.Contains(trg, triggers);
        }

        // The external refs carry no foreign key (Option A: scalar columns).
        var evidenceFks = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.key_column_usage "
            + "WHERE table_schema = DATABASE() AND table_name = 'evidence_runs' "
            + "AND referenced_table_name IS NOT NULL;");
        Assert.Equal(0, evidenceFks);

        // The internal check FK references evidence_runs.
        var checkFk = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.key_column_usage "
            + "WHERE table_schema = DATABASE() AND table_name = 'evidence_checks' "
            + "AND referenced_table_name = 'evidence_runs';");
        Assert.Equal(1, checkFk);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task AppendPersistsRunWithChecks()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        var writes = new MySqlEvidenceWriteStore(db.ConnectionFactory, new UlidFactory());
        var store = new MySqlEvidenceStore(db.ConnectionFactory);

        var result = await writes.AppendEvidenceAsync(Run(
            "org-a", "req-a", "vendor-a", "ref-1", "Fail", rawPayload: "{\"k\":1}",
            checks: [Check("tls", "Hard", "Fail", "expired cert"), Check("logging", "Soft", "Pass")]));
        Assert.True(result.Ok, result.Error);

        var run = await store.GetLatestEvidenceRunAsync("org-a", "req-a");
        Assert.NotNull(run);
        Assert.Equal("Collector", run!.Kind);
        Assert.Equal("vendor-a", run.Vendor);
        Assert.Equal("Fail", run.Result);
        Assert.Null(run.Attestation);
        Assert.Equal(["tls", "logging"], run.Checks.Select(c => c.Name).ToArray());
        Assert.Equal([0, 1], run.Checks.Select(c => c.Ordinal).ToArray());
        Assert.Equal("expired cert", run.Checks[0].Detail);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task AttestationAppendPersistsExtensionAndChecks()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        var writes = new MySqlEvidenceWriteStore(db.ConnectionFactory, new UlidFactory());
        var store = new MySqlEvidenceStore(db.ConnectionFactory);

        var result = await writes.AppendAttestationResponseAsync(
            Run("org-a", "req-a", "quiz-system", "submission-1", "Pass",
                checks: [Check("q1", "Hard", "Pass"), Check("q2", "Soft", "Pass")]),
            new NewAttestationResponse("user-1", QuizPassed: true, Score: 90));
        Assert.True(result.Ok, result.Error);

        var run = await store.GetLatestEvidenceRunAsync("org-a", "req-a");
        Assert.NotNull(run);
        Assert.Equal("AttestationResponse", run!.Kind);
        Assert.NotNull(run.Attestation);
        Assert.Equal("user-1", run.Attestation!.UserId);
        Assert.True(run.Attestation.QuizPassed);
        Assert.Equal(90, run.Attestation.Score);
        Assert.Equal(2, run.Checks.Count);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task DuplicateCheckNameFails()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        var writes = new MySqlEvidenceWriteStore(db.ConnectionFactory, new UlidFactory());
        var store = new MySqlEvidenceStore(db.ConnectionFactory);

        var result = await writes.AppendEvidenceAsync(Run(
            "org-a", "req-a", "vendor-a", "ref-1",
            checks: [Check("dup", "Hard", "Pass"), Check("dup", "Soft", "Pass")]));
        Assert.False(result.Ok);

        // The whole append rolled back: no run persisted.
        Assert.Empty(await store.GetEvidenceRunsAsync("org-a", "req-a"));
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task DuplicateVendorCollectorRefFails()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        var writes = new MySqlEvidenceWriteStore(db.ConnectionFactory, new UlidFactory());
        var store = new MySqlEvidenceStore(db.ConnectionFactory);

        Assert.True((await writes.AppendEvidenceAsync(Run("org-a", "req-a", "vendor-a", "ref-1"))).Ok);
        var dup = await writes.AppendEvidenceAsync(Run("org-a", "req-a", "vendor-a", "ref-1"));
        Assert.False(dup.Ok);
        // The ingest endpoint's 200-replay-vs-422 branch depends on a duplicate mapping to IsConflict.
        Assert.True(dup.IsConflict);

        // Only the first append survives.
        Assert.Single(await store.GetEvidenceRunsAsync("org-a", "req-a"));
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task InvalidValuesRejectedAndWriteNothing()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        var writes = new MySqlEvidenceWriteStore(db.ConnectionFactory, new UlidFactory());
        var store = new MySqlEvidenceStore(db.ConnectionFactory);

        Assert.False((await writes.AppendEvidenceAsync(Run("org-a", "req-a", "v", "r", "Sideways"))).Ok);
        Assert.False((await writes.AppendEvidenceAsync(Run("org-a", "req-a", "", "r"))).Ok);
        Assert.False((await writes.AppendEvidenceAsync(Run(
            "org-a", "req-a", "v", "r", checks: [Check("c", "Critical", "Pass")]))).Ok);
        Assert.Empty(await store.GetEvidenceRunsAsync("org-a", "req-a"));
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task RawUpdateAndDeleteRejectedByTriggers()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        var writes = new MySqlEvidenceWriteStore(db.ConnectionFactory, new UlidFactory());
        // Attestation run supplies a check row and an attestation row for their own delete/update tests.
        Assert.True((await writes.AppendAttestationResponseAsync(
            Run("org-a", "req-a", "vendor-a", "ref-1", checks: [Check("c", "Hard", "Pass")]),
            new NewAttestationResponse("user-1", true, null))).Ok);
        // A childless run so the evidence_runs DELETE can only be blocked by the append-only trigger,
        // never by the evidence_checks / attestation_responses ON DELETE RESTRICT foreign key.
        Assert.True((await writes.AppendEvidenceAsync(Run("org-b", "req-b", "vendor-b", "ref-childless"))).Ok);

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var childlessId = await conn.ExecuteScalarAsync<string>(
            "SELECT id FROM evidence_runs WHERE collector_ref = 'ref-childless';");

        // Each table rejects a raw UPDATE and a raw DELETE. The evidence_runs DELETE targets the childless
        // run, and every assertion confirms SQLSTATE 45000 with the trigger's message, so the failure
        // proves the append-only trigger fired rather than an incidental FK RESTRICT.
        await AssertAppendOnlyBlockedAsync(conn, "UPDATE evidence_runs SET result = 'Pass' WHERE id = @id;", childlessId);
        await AssertAppendOnlyBlockedAsync(conn, "DELETE FROM evidence_runs WHERE id = @id;", childlessId);
        await AssertAppendOnlyBlockedAsync(conn, "UPDATE evidence_checks SET result = 'Fail';");
        await AssertAppendOnlyBlockedAsync(conn, "DELETE FROM evidence_checks;");
        await AssertAppendOnlyBlockedAsync(conn, "UPDATE attestation_responses SET score = 1;");
        await AssertAppendOnlyBlockedAsync(conn, "DELETE FROM attestation_responses;");
    }

    private static async Task AssertAppendOnlyBlockedAsync(MySqlConnection conn, string sql, string? id = null)
    {
        var ex = await Assert.ThrowsAsync<MySqlException>(() =>
            id is null ? conn.ExecuteAsync(sql) : conn.ExecuteAsync(sql, new { id }));
        Assert.Equal("45000", ex.SqlState);
        Assert.Contains("evidence is append-only", ex.Message, StringComparison.Ordinal);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task EvidenceSurvivesDeletionOfReferencedRequirementAndOrganisation()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        // Seed a real organisation and requirement, then append evidence referencing them.
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            "INSERT INTO standards (id, api_version, title, created_at, updated_at) VALUES ('std', 'v1', 'S', NOW(6), NOW(6));");
        await conn.ExecuteAsync(
            "INSERT INTO requirements (id, api_version, title, standard_id, theme, statement, citation_label, citation_url, created_at, updated_at) "
            + "VALUES ('req-a', 'v1', 'R', 'std', 'T', 'S', 'L', 'https://example.com/r', NOW(6), NOW(6));");
        await conn.ExecuteAsync(
            "INSERT INTO organisations (id, api_version, title, kind, created_at, updated_at) VALUES ('org-a', 'v1', 'O', 'Company', NOW(6), NOW(6));");

        var writes = new MySqlEvidenceWriteStore(db.ConnectionFactory, new UlidFactory());
        var store = new MySqlEvidenceStore(db.ConnectionFactory);
        Assert.True((await writes.AppendEvidenceAsync(Run("org-a", "req-a", "vendor-a", "ref-1"))).Ok);

        // Scalar refs, no FK: dropping the requirement and organisation does not touch the evidence row.
        await conn.ExecuteAsync("DELETE FROM requirements WHERE id = 'req-a';");
        await conn.ExecuteAsync("DELETE FROM organisations WHERE id = 'org-a';");

        Assert.Single(await store.GetEvidenceRunsAsync("org-a", "req-a"));
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task AssessmentDerivesHardSoftAndPassing()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        var writes = new MySqlEvidenceWriteStore(db.ConnectionFactory, new UlidFactory());
        var store = new MySqlEvidenceStore(db.ConnectionFactory);

        // req-hard: a failing Hard check dominates a passing Soft => HardFailure.
        Assert.True((await writes.AppendEvidenceAsync(Run(
            "org-a", "req-hard", "v", "ref-hard",
            checks: [Check("h", "Hard", "Fail"), Check("s", "Soft", "Pass")]))).Ok);
        // req-soft: no failing Hard, one failing Soft => SoftFailure.
        Assert.True((await writes.AppendEvidenceAsync(Run(
            "org-a", "req-soft", "v", "ref-soft",
            checks: [Check("h", "Hard", "Pass"), Check("s", "Soft", "Fail")]))).Ok);
        // req-pass: all checks pass => Passing.
        Assert.True((await writes.AppendEvidenceAsync(Run(
            "org-a", "req-pass", "v", "ref-pass",
            checks: [Check("h", "Hard", "Pass"), Check("s", "Soft", "Pass")]))).Ok);

        var results = (await store.GetAssessmentResultsAsync("org-a"))
            .ToDictionary(r => r.RequirementId, r => r.Status, StringComparer.Ordinal);

        Assert.Equal("HardFailure", results["req-hard"]);
        Assert.Equal("SoftFailure", results["req-soft"]);
        Assert.Equal("Passing", results["req-pass"]);
        // A pair with no evidence yields no store status (the store never emits NoEvidence).
        Assert.False(results.ContainsKey("req-absent"));
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task OnlyLatestRunCountsWhileEarlierRunStaysUnchanged()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        var writes = new MySqlEvidenceWriteStore(db.ConnectionFactory, new UlidFactory());
        var store = new MySqlEvidenceStore(db.ConnectionFactory);

        var earlier = DateTime.UtcNow.AddHours(-1);
        var later = DateTime.UtcNow;

        // Earlier run fails hard.
        Assert.True((await writes.AppendEvidenceAsync(Run(
            "org-a", "req-a", "v", "ref-old", collectedAt: earlier,
            checks: [Check("h", "Hard", "Fail")]))).Ok);
        // Later run passes: it must win the assessment.
        Assert.True((await writes.AppendEvidenceAsync(Run(
            "org-a", "req-a", "v", "ref-new", collectedAt: later,
            checks: [Check("h", "Hard", "Pass")]))).Ok);

        var results = await store.GetAssessmentResultsAsync("org-a");
        Assert.Equal("Passing", Assert.Single(results).Status);

        // Both runs are still present; the earlier one is unchanged (append-only history).
        var runs = await store.GetEvidenceRunsAsync("org-a", "req-a");
        Assert.Equal(2, runs.Count);
        Assert.Equal("ref-new", runs[0].CollectorRef); // newest first
        Assert.Equal("Fail", runs[1].Checks.Single().Result);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task LatestRunTieBreakFollowsFullOrdering()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var c = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // Latest is ordered by collected_at, then received_at, then created_at, then id (all DESC). Each
        // row wins exactly one level while losing every lower-priority key, and the ids run counter to the
        // higher keys, so dropping any ORDER BY term reorders the result. Inserts set created_at and id
        // directly, which the write store does not expose.
        await InsertRunAsync(conn, Id(10), collected: c.AddSeconds(1), received: c, created: c, collectorRef: "r1");
        await InsertRunAsync(conn, Id(11), collected: c, received: c.AddSeconds(1), created: c, collectorRef: "r2");
        await InsertRunAsync(conn, Id(1), collected: c, received: c, created: c.AddSeconds(1), collectorRef: "r3");
        await InsertRunAsync(conn, Id(3), collected: c, received: c, created: c, collectorRef: "r4");
        await InsertRunAsync(conn, Id(2), collected: c, received: c, created: c, collectorRef: "r5");

        var store = new MySqlEvidenceStore(db.ConnectionFactory);
        var runs = await store.GetEvidenceRunsAsync("org-tie", "req-tie");
        Assert.Equal(["r1", "r2", "r3", "r4", "r5"], runs.Select(x => x.CollectorRef).ToArray());

        var latest = await store.GetLatestEvidenceRunAsync("org-tie", "req-tie");
        Assert.Equal("r1", latest!.CollectorRef);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task InvalidRawPayloadJsonRejectedAndWritesNothing()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        var writes = new MySqlEvidenceWriteStore(db.ConnectionFactory, new UlidFactory());
        var store = new MySqlEvidenceStore(db.ConnectionFactory);

        var result = await writes.AppendEvidenceAsync(Run(
            "org-a", "req-a", "vendor-a", "ref-1", rawPayload: "not json"));
        Assert.False(result.Ok);

        Assert.Empty(await store.GetEvidenceRunsAsync("org-a", "req-a"));
    }

    private static Task InsertRunAsync(
        MySqlConnection conn, string id, DateTime collected, DateTime received, DateTime created, string collectorRef) =>
        conn.ExecuteAsync(
            "INSERT INTO evidence_runs (id, kind, organisation_id, requirement_id, vendor, collector_ref, "
            + "result, collected_at, received_at, raw_payload, created_at) "
            + "VALUES (@Id, 'Collector', 'org-tie', 'req-tie', 'vendor-tie', @CollectorRef, 'Pass', "
            + "@Collected, @Received, NULL, @Created);",
            new { Id = id, CollectorRef = collectorRef, Collected = collected, Received = received, Created = created });

    // A unique, order-preserving 26-char id (CHAR(26) utf8mb4_bin sorts by exact bytes).
    private static string Id(int n) => n.ToString("D26");

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task Migration011ReplayIsIdempotent()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        // ApplyPendingAsync records applied migrations and will not re-run 011, so execute the raw SQL
        // text directly a second time. IF NOT EXISTS tables and DROP-then-CREATE triggers make it
        // re-runnable.
        var raw = await ReadMigration011Async();
        await conn.ExecuteAsync(raw);

        var triggerCount = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.triggers WHERE trigger_schema = DATABASE() "
            + "AND (trigger_name LIKE 'trg_evidence%' OR trigger_name LIKE 'trg_attestation%');");
        Assert.Equal(6, triggerCount);
    }

    private static async Task<string> ReadMigration011Async()
    {
        var asm = typeof(IMigrationRunner).Assembly;
        var name = asm.GetManifestResourceNames().Single(n => n.EndsWith("011_evidence.sql", StringComparison.Ordinal));
        await using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}
