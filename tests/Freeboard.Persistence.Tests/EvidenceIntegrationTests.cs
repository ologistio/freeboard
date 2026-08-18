using Dapper;
using Freeboard.Persistence;
using Freeboard.Persistence.Auth;
using Freeboard.Persistence.System;
using Freeboard.TestInfrastructure;
using MySqlConnector;

namespace Freeboard.Persistence.Tests;

/// <summary>
/// Integration tests for the evidence schema (migrations 011, 015, and 024), the append-only store pair,
/// both idempotency keys, and the computed per-collector evidence status (including Stale and Errored),
/// against a real MySQL discovered via FREEBOARD_TEST_DB. Each test SKIPS cleanly when the env var is
/// absent.
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
        string? vendor,
        string? collectorRef,
        string result = "Pass",
        DateTime? collectedAt = null,
        DateTime? receivedAt = null,
        string? rawPayload = null,
        string? collectorId = null,
        string? frequency = null,
        string? assetId = null,
        string? cycleId = null,
        string? errorDetail = null,
        params NewEvidenceCheck[] checks) =>
        new(org, requirement, vendor, collectorRef, result,
            collectedAt ?? DateTime.UtcNow, receivedAt, rawPayload, checks, collectorId, frequency,
            assetId, cycleId, errorDetail);

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

        // Seed a real Company asset and requirement, then append evidence referencing them.
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            "INSERT INTO standards (id, api_version, title, created_at, updated_at) VALUES ('std', 'v1', 'S', NOW(6), NOW(6));");
        await conn.ExecuteAsync(
            "INSERT INTO requirements (id, api_version, title, standard_id, theme, statement, citation_label, citation_url, created_at, updated_at) "
            + "VALUES ('req-a', 'v1', 'R', 'std', 'T', 'S', 'L', 'https://example.com/r', NOW(6), NOW(6));");
        await conn.ExecuteAsync(
            "INSERT INTO assets (id, type, source, api_version, title, created_at, updated_at) "
            + "VALUES ('org-a', 'Company', 'declared', 'v1', 'O', NOW(6), NOW(6));");

        var writes = new MySqlEvidenceWriteStore(db.ConnectionFactory, new UlidFactory());
        var store = new MySqlEvidenceStore(db.ConnectionFactory);
        Assert.True((await writes.AppendEvidenceAsync(Run("org-a", "req-a", "vendor-a", "ref-1"))).Ok);

        // Scalar refs, no FK: dropping the requirement and the asset does not touch the evidence row.
        await conn.ExecuteAsync("DELETE FROM requirements WHERE id = 'req-a';");
        await conn.ExecuteAsync("DELETE FROM assets WHERE id = 'org-a';");

        Assert.Single(await store.GetEvidenceRunsAsync("org-a", "req-a"));
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task StatusDerivesHardSoftPassingAndStalePerCollector()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        var writes = new MySqlEvidenceWriteStore(db.ConnectionFactory, new UlidFactory());
        var store = new MySqlEvidenceStore(db.ConnectionFactory);

        var fresh = DateTime.UtcNow;
        var overdue = DateTime.UtcNow.AddDays(-2); // daily window+grace is 30h, so 2d is stale

        // A fresh daily collector, all checks pass => Passing.
        Assert.True((await writes.AppendEvidenceAsync(Run(
            "org-a", "req-pass", "v", "coll-pass:r1", collectorId: "coll-pass", frequency: "daily",
            collectedAt: fresh, checks: [Check("h", "Hard", "Pass"), Check("s", "Soft", "Pass")]))).Ok);
        // A fresh daily collector, failing Soft => SoftFailure.
        Assert.True((await writes.AppendEvidenceAsync(Run(
            "org-a", "req-soft", "v", "coll-soft:r1", collectorId: "coll-soft", frequency: "daily",
            collectedAt: fresh, checks: [Check("h", "Hard", "Pass"), Check("s", "Soft", "Fail")]))).Ok);
        // An overdue daily collector whose checks pass => downgraded to Stale.
        Assert.True((await writes.AppendEvidenceAsync(Run(
            "org-a", "req-stale", "v", "coll-stale:r1", collectorId: "coll-stale", frequency: "daily",
            collectedAt: overdue, checks: [Check("h", "Hard", "Pass")]))).Ok);
        // An overdue daily collector that also hard-fails stays HardFailure (Stale sits below HardFailure).
        Assert.True((await writes.AppendEvidenceAsync(Run(
            "org-a", "req-hardstale", "v", "coll-hs:r1", collectorId: "coll-hs", frequency: "daily",
            collectedAt: overdue, checks: [Check("h", "Hard", "Fail")]))).Ok);
        // An overdue run with NO recorded cadence is never Stale: it keeps its passing verdict.
        Assert.True((await writes.AppendEvidenceAsync(Run(
            "org-a", "req-nocadence", "v", "coll-nc:r1", collectorId: "coll-nc", frequency: null,
            collectedAt: overdue, checks: [Check("h", "Hard", "Pass")]))).Ok);

        var results = (await store.GetCollectorEvidenceStatusesAsync(["org-a"]))
            .ToDictionary(r => r.CollectorId, r => r.Status, StringComparer.Ordinal);

        Assert.Equal("Passing", results["coll-pass"]);
        Assert.Equal("SoftFailure", results["coll-soft"]);
        Assert.Equal("Stale", results["coll-stale"]);
        Assert.Equal("HardFailure", results["coll-hs"]);
        Assert.Equal("Passing", results["coll-nc"]);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task LegacyNullCollectorIdIsAttributedByCollectorRefPrefix()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        // A pre-migration-shaped row: null collector_id and null frequency, collector_ref = id:run.
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var runId = Id(1);
        await conn.ExecuteAsync(
            "INSERT INTO evidence_runs (id, kind, organisation_id, requirement_id, vendor, collector_ref, "
            + "result, collected_at, received_at, raw_payload, created_at, collector_id, frequency) "
            + "VALUES (@Id, 'Collector', 'org-a', 'req-a', 'v', 'legacy-coll:run1', 'Pass', "
            + "@Old, NULL, NULL, @Old, NULL, NULL);",
            new { Id = runId, Old = DateTime.UtcNow.AddDays(-400) });

        var store = new MySqlEvidenceStore(db.ConnectionFactory);
        var results = await store.GetCollectorEvidenceStatusesAsync(["org-a"]);

        var row = Assert.Single(results);
        // Identity recovered from the collector_ref prefix; null cadence keeps it out of Stale despite age.
        Assert.Equal("legacy-coll", row.CollectorId);
        Assert.Equal("Passing", row.Status);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task RunsWithNoRecoverableCollectorIdentityAreNotAttributed()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        // A legacy Collector run whose collector_ref has no ':' delimiter: no first-class collector_id and
        // no prefix to recover, so it has no collector identity to attribute.
        await conn.ExecuteAsync(
            "INSERT INTO evidence_runs (id, kind, organisation_id, requirement_id, vendor, collector_ref, "
            + "result, collected_at, received_at, raw_payload, created_at, collector_id, frequency) "
            + "VALUES (@Id, 'Collector', 'org-a', 'req-a', 'v', 'legacy-no-delimiter', 'Pass', "
            + "@Now, NULL, NULL, @Now, NULL, NULL);",
            new { Id = Id(1), Now = DateTime.UtcNow });

        // A legacy Collector run whose collector_ref STARTS with ':': the delimiter is there but the
        // prefix before it is empty, which names no collector. The empty string must not be recovered as
        // an identity, or the group would report a phantom collector under an empty id.
        await conn.ExecuteAsync(
            "INSERT INTO evidence_runs (id, kind, organisation_id, requirement_id, vendor, collector_ref, "
            + "result, collected_at, received_at, raw_payload, created_at, collector_id, frequency) "
            + "VALUES (@Id, 'Collector', 'org-a', 'req-a', 'v', ':run1', 'Pass', "
            + "@Now, NULL, NULL, @Now, NULL, NULL);",
            new { Id = Id(2), Now = DateTime.UtcNow });

        // A non-Collector run whose collector_ref does contain a ':': its prefix must not be mined for a
        // collector identity, because per-collector status covers Collector-kind runs only.
        var writes = new MySqlEvidenceWriteStore(db.ConnectionFactory, new UlidFactory());
        Assert.True((await writes.AppendAttestationResponseAsync(
            Run("org-a", "req-a", "quiz-system", "attn-coll:submission-1",
                checks: [Check("q1", "Hard", "Pass")]),
            new NewAttestationResponse("user-1", QuizPassed: true, Score: null))).Ok);

        var store = new MySqlEvidenceStore(db.ConnectionFactory);
        Assert.Empty(await store.GetCollectorEvidenceStatusesAsync(["org-a"]));
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task LatestRunPerCollectorFollowsTieBreakOrdering()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        // Two runs of one collector share collected_at, so the id tie-break (ULID, DESC) decides which run
        // pins the group's status. The higher id passes; if the pin fell to the lower id the status would
        // be HardFailure, so a Passing result proves the tie-break carries into the per-collector pin.
        // Recent, well inside the daily window (24h + 6h grace = 30h), so the correctly-picked higher-id
        // run reads Passing rather than being downgraded to Stale by age - isolating the tie-break from
        // staleness. id is the only field that differs between the two runs.
        var collected = DateTime.UtcNow.AddMinutes(-5);
        await conn.ExecuteAsync(
            "INSERT INTO evidence_runs (id, kind, organisation_id, requirement_id, vendor, collector_ref, "
            + "result, collected_at, received_at, raw_payload, created_at, collector_id, frequency) "
            + "VALUES (@Id, 'Collector', 'org-a', 'req-a', 'v', @Ref, 'Pass', @C, @C, NULL, @C, 'coll-x', 'daily');",
            new[]
            {
                new { Id = Id(1), Ref = "coll-x:low", C = collected },
                new { Id = Id(2), Ref = "coll-x:high", C = collected },
            });
        await conn.ExecuteAsync(
            "INSERT INTO evidence_checks (id, evidence_id, name, severity, result, ordinal, detail) "
            + "VALUES (@Id, @Rid, 'h', 'Hard', @Result, 0, NULL);",
            new[]
            {
                new { Id = Id(3), Rid = Id(1), Result = "Fail" },
                new { Id = Id(4), Rid = Id(2), Result = "Pass" },
            });

        var store = new MySqlEvidenceStore(db.ConnectionFactory);
        var row = Assert.Single(await store.GetCollectorEvidenceStatusesAsync(["org-a"]));
        Assert.Equal("coll-x", row.CollectorId);
        Assert.Equal("Passing", row.Status);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task TwoCollectorsOnOneRequirementDoNotHideEachOther()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        var writes = new MySqlEvidenceWriteStore(db.ConnectionFactory, new UlidFactory());
        var store = new MySqlEvidenceStore(db.ConnectionFactory);

        // Two collectors on the same requirement: one fresh, one stale. The fresh one must not hide the
        // stale one - per-collector grouping surfaces both statuses.
        Assert.True((await writes.AppendEvidenceAsync(Run(
            "org-a", "req-a", "v", "coll-fresh:r1", collectorId: "coll-fresh", frequency: "daily",
            collectedAt: DateTime.UtcNow, checks: [Check("h", "Hard", "Pass")]))).Ok);
        Assert.True((await writes.AppendEvidenceAsync(Run(
            "org-a", "req-a", "v", "coll-stopped:r1", collectorId: "coll-stopped", frequency: "daily",
            collectedAt: DateTime.UtcNow.AddDays(-2), checks: [Check("h", "Hard", "Pass")]))).Ok);

        var results = (await store.GetCollectorEvidenceStatusesAsync(["org-a"]))
            .ToDictionary(r => r.CollectorId, r => r.Status, StringComparer.Ordinal);

        Assert.Equal("Passing", results["coll-fresh"]);
        Assert.Equal("Stale", results["coll-stopped"]);
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
            "org-a", "req-a", "v", "coll-x:old", collectorId: "coll-x", frequency: "daily", collectedAt: earlier,
            checks: [Check("h", "Hard", "Fail")]))).Ok);
        // Later run of the same collector passes: it must win the status.
        Assert.True((await writes.AppendEvidenceAsync(Run(
            "org-a", "req-a", "v", "coll-x:new", collectorId: "coll-x", frequency: "daily", collectedAt: later,
            checks: [Check("h", "Hard", "Pass")]))).Ok);

        var results = await store.GetCollectorEvidenceStatusesAsync(["org-a"]);
        Assert.Equal("Passing", Assert.Single(results).Status);

        // Both runs are still present; the earlier one is unchanged (append-only history).
        var runs = await store.GetEvidenceRunsAsync("org-a", "req-a");
        Assert.Equal(2, runs.Count);
        Assert.Equal("coll-x:new", runs[0].CollectorRef); // newest first
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
        Assert.Equal(["r1", "r2", "r3", "r4", "r5"], runs.Select(x => x.CollectorRef!).ToArray());

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

    private static Task<string> ReadMigration011Async() => ReadMigrationAsync("011_evidence.sql");

    private static async Task<string> ReadMigrationAsync(string fileSuffix)
    {
        var asm = typeof(IMigrationRunner).Assembly;
        var name = asm.GetManifestResourceNames().Single(n => n.EndsWith(fileSuffix, StringComparison.Ordinal));
        await using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task Migration015AddsNullableColumnsWithoutBackfill()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        // Return the table to its pre-015 shape, then seed a legacy collector run and its check as they
        // would exist before the additive migration ran. MySQL refuses to drop a column that a check
        // constraint names, so the identity constraint over collector_id comes off first and is restored
        // once 015 has put the column back.
        await conn.ExecuteAsync("ALTER TABLE evidence_runs DROP CHECK ck_evidence_runs_cycle_identity;");
        await conn.ExecuteAsync("ALTER TABLE evidence_runs DROP COLUMN collector_id, DROP COLUMN frequency;");
        var collected = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var runId = Id(1);
        await conn.ExecuteAsync(
            "INSERT INTO evidence_runs (id, kind, organisation_id, requirement_id, vendor, collector_ref, "
            + "result, collected_at, received_at, raw_payload, created_at) "
            + "VALUES (@Id, 'Collector', 'org-a', 'req-a', 'v', 'legacy-coll:run1', 'Pass', @Collected, NULL, NULL, @Collected);",
            new { Id = runId, Collected = collected });
        await conn.ExecuteAsync(
            "INSERT INTO evidence_checks (id, evidence_id, name, severity, result, ordinal, detail) "
            + "VALUES (@Cid, @Rid, 'c', 'Hard', 'Pass', 0, NULL);",
            new { Cid = Id(2), Rid = runId });

        // Run 015 raw against the table that already holds the legacy row.
        await conn.ExecuteAsync(await ReadMigrationAsync("015_evidence_collector_identity.sql"));
        await conn.ExecuteAsync(
            "ALTER TABLE evidence_runs ADD CONSTRAINT ck_evidence_runs_cycle_identity CHECK ("
            + "(vendor IS NOT NULL AND collector_ref IS NOT NULL AND cycle_id IS NULL) "
            + "OR (collector_id IS NOT NULL AND TRIM(collector_id) <> _utf8mb4'' "
            + "AND cycle_id IS NOT NULL AND TRIM(cycle_id) <> _utf8mb4'' "
            + "AND vendor IS NULL AND collector_ref IS NULL));");

        // Both columns exist and are nullable.
        var columns = (await conn.QueryAsync<(string ColumnName, string IsNullable)>(
            "SELECT column_name AS ColumnName, is_nullable AS IsNullable FROM information_schema.columns "
            + "WHERE table_schema = DATABASE() AND table_name = 'evidence_runs' "
            + "AND column_name IN ('collector_id', 'frequency');"))
            .ToDictionary(c => c.ColumnName, c => c.IsNullable, StringComparer.Ordinal);
        Assert.Equal("YES", columns["collector_id"]);
        Assert.Equal("YES", columns["frequency"]);

        // No backfill: the legacy row stays null on both new columns, and its prior fields are unchanged.
        var row = await conn.QuerySingleAsync<(string? CollectorId, string? Frequency, string Result, DateTime Collected, string CollectorRef)>(
            "SELECT collector_id AS CollectorId, frequency AS Frequency, result AS Result, "
            + "collected_at AS Collected, collector_ref AS CollectorRef FROM evidence_runs WHERE id = @Id;",
            new { Id = runId });
        Assert.Null(row.CollectorId);
        Assert.Null(row.Frequency);
        Assert.Equal("Pass", row.Result);
        Assert.Equal(collected, row.Collected);
        Assert.Equal("legacy-coll:run1", row.CollectorRef);

        // The check row is untouched.
        var checkResult = await conn.ExecuteScalarAsync<string>(
            "SELECT result FROM evidence_checks WHERE evidence_id = @Id;", new { Id = runId });
        Assert.Equal("Pass", checkResult);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task Migration024AddsTheMachineCycleAndErrorShape()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var columns = (await conn.QueryAsync<(string ColumnName, string IsNullable, string Extra)>(
            "SELECT column_name AS ColumnName, is_nullable AS IsNullable, extra AS Extra "
            + "FROM information_schema.columns WHERE table_schema = DATABASE() "
            + "AND table_name = 'evidence_runs';"))
            .ToDictionary(c => c.ColumnName, c => c, StringComparer.Ordinal);

        // The three recorded columns and the generated key part are all nullable and additive.
        foreach (var added in new[] { "asset_id", "cycle_id", "error_detail", "asset_key" })
        {
            Assert.Equal("YES", columns[added].IsNullable);
        }

        Assert.Contains("GENERATED", columns["asset_key"].Extra, StringComparison.Ordinal);

        // Relaxed so an in-process run need not fabricate a producer identity it does not have.
        Assert.Equal("YES", columns["vendor"].IsNullable);
        Assert.Equal("YES", columns["collector_ref"].IsNullable);

        var cycleKey = (await conn.QueryAsync<string>(
            "SELECT column_name FROM information_schema.statistics "
            + "WHERE table_schema = DATABASE() AND table_name = 'evidence_runs' "
            + "AND index_name = 'uq_evidence_runs_cycle' AND non_unique = 0 ORDER BY seq_in_index;")).ToArray();
        Assert.Equal(["cycle_id", "organisation_id", "requirement_id", "asset_key"], cycleKey);

        var constraints = (await conn.QueryAsync<string>(
            "SELECT constraint_name FROM information_schema.table_constraints "
            + "WHERE table_schema = DATABASE() AND table_name = 'evidence_runs' "
            + "AND constraint_type = 'CHECK';")).ToHashSet(StringComparer.Ordinal);
        foreach (var name in new[]
                 {
                     "ck_evidence_runs_result", "ck_evidence_runs_error_detail",
                     "ck_evidence_runs_ref_pair", "ck_evidence_runs_cycle_identity",
                 })
        {
            Assert.Contains(name, constraints);
        }
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task ACycleKeyedRunRecordsItsMachineAndDedupsPerMachine()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        var writes = new MySqlEvidenceWriteStore(db.ConnectionFactory, new UlidFactory());
        var store = new MySqlEvidenceStore(db.ConnectionFactory);

        // Two machines in one cycle both store: they differ in the generated asset_key.
        Assert.True((await writes.AppendEvidenceAsync(Run(
            "org-a", "req-a", null, null, collectorId: "coll-a", frequency: "daily",
            assetId: "machine-1", cycleId: "cycle-1", checks: [Check("h", "Hard", "Pass")]))).Ok);
        Assert.True((await writes.AppendEvidenceAsync(Run(
            "org-a", "req-a", null, null, collectorId: "coll-a", frequency: "daily",
            assetId: "machine-2", cycleId: "cycle-1", checks: [Check("h", "Hard", "Pass")]))).Ok);

        var runs = await store.GetEvidenceRunsAsync("org-a", "req-a");
        Assert.Equal(2, runs.Count);
        Assert.Equal(["machine-1", "machine-2"], runs.Select(r => r.AssetId!).Order(StringComparer.Ordinal).ToArray());
        // The machine is a dimension under the organisation, never a replacement for it.
        Assert.All(runs, r => Assert.Equal("org-a", r.OrganisationId));
        Assert.All(runs, r => Assert.Equal("cycle-1", r.CycleId));
        Assert.All(runs, r => Assert.Null(r.Vendor));

        // A recorded cycle run is final: the retry collides and the recorded result stands.
        var repeat = await writes.AppendEvidenceAsync(Run(
            "org-a", "req-a", null, null, "Fail", collectorId: "coll-a", frequency: "daily",
            assetId: "machine-1", cycleId: "cycle-1", checks: [Check("h", "Hard", "Fail")]));
        Assert.False(repeat.Ok);
        Assert.Equal(2, (await store.GetEvidenceRunsAsync("org-a", "req-a")).Count);
        Assert.All(await store.GetEvidenceRunsAsync("org-a", "req-a"), r => Assert.Equal("Pass", r.Result));

        // The same machine in a DIFFERENT cycle is a different run.
        Assert.True((await writes.AppendEvidenceAsync(Run(
            "org-a", "req-a", null, null, collectorId: "coll-a", frequency: "daily",
            assetId: "machine-1", cycleId: "cycle-2", checks: [Check("h", "Hard", "Pass")]))).Ok);
        Assert.Equal(3, (await store.GetEvidenceRunsAsync("org-a", "req-a")).Count);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task TwoOrganisationLevelRunsInOneCycleCollideOnTheGeneratedKey()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        var writes = new MySqlEvidenceWriteStore(db.ConnectionFactory, new UlidFactory());
        var store = new MySqlEvidenceStore(db.ConnectionFactory);

        // Both carry a null asset_id, so both generate the empty asset_key and the key dedups them.
        // Indexing asset_id itself would not, because MySQL treats each NULL as distinct.
        Assert.True((await writes.AppendEvidenceAsync(Run(
            "org-a", "req-a", null, null, collectorId: "coll-a", cycleId: "cycle-1"))).Ok);
        Assert.False((await writes.AppendEvidenceAsync(Run(
            "org-a", "req-a", null, null, collectorId: "coll-a", cycleId: "cycle-1"))).Ok);

        Assert.Single(await store.GetEvidenceRunsAsync("org-a", "req-a"));
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task AnErroredRunRecordsWhyCollectionFailed()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        var writes = new MySqlEvidenceWriteStore(db.ConnectionFactory, new UlidFactory());
        var store = new MySqlEvidenceStore(db.ConnectionFactory);

        // An error usually observed nothing, so it carries no checks.
        Assert.True((await writes.AppendEvidenceAsync(Run(
            "org-a", "req-a", "v", "ref-error", "Error", errorDetail: "token could not be resolved"))).Ok);
        // A partial collection observed some checks before it failed, and keeps them.
        Assert.True((await writes.AppendEvidenceAsync(Run(
            "org-a", "req-a", "v", "ref-partial", "Error", errorDetail: "provider closed the connection",
            checks: [Check("h", "Hard", "Pass")]))).Ok);

        var runs = await store.GetEvidenceRunsAsync("org-a", "req-a");
        Assert.Equal(2, runs.Count);
        Assert.All(runs, r => Assert.Equal("Error", r.Result));
        Assert.Contains(runs, r => r.ErrorDetail == "token could not be resolved" && r.Checks.Count == 0);
        Assert.Contains(runs, r => r.ErrorDetail == "provider closed the connection" && r.Checks.Count == 1);

        // Recording an error with no reason is not recording it, and a blank reason is no reason.
        Assert.False((await writes.AppendEvidenceAsync(Run(
            "org-a", "req-a", "v", "ref-nodetail", "Error"))).Ok);
        Assert.False((await writes.AppendEvidenceAsync(Run(
            "org-a", "req-a", "v", "ref-blank", "Error", errorDetail: " "))).Ok);
        // The detail belongs to an errored run alone, so the column means one thing.
        Assert.False((await writes.AppendEvidenceAsync(Run(
            "org-a", "req-a", "v", "ref-passdetail", errorDetail: "why?"))).Ok);

        Assert.Equal(2, (await store.GetEvidenceRunsAsync("org-a", "req-a")).Count);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task AnAppendCarriesExactlyOneIdentity()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        var writes = new MySqlEvidenceWriteStore(db.ConnectionFactory, new UlidFactory());
        var store = new MySqlEvidenceStore(db.ConnectionFactory);

        // Neither key could dedup this run.
        Assert.False((await writes.AppendEvidenceAsync(Run("org-a", "req-a", null, null))).Ok);
        // Both keys would claim this one, so "was this a duplicate" would have two answers.
        Assert.False((await writes.AppendEvidenceAsync(Run(
            "org-a", "req-a", "v", "ref-1", collectorId: "coll-a", cycleId: "cycle-1"))).Ok);
        // A half-identified run would escape the legacy key, which does not dedup on a null part.
        Assert.False((await writes.AppendEvidenceAsync(Run("org-a", "req-a", "v", null))).Ok);
        Assert.False((await writes.AppendEvidenceAsync(Run("org-a", "req-a", null, "ref-1"))).Ok);
        // Whitespace is absence: this is a run with no vendor, not a run with a vendor of one space.
        Assert.False((await writes.AppendEvidenceAsync(Run("org-a", "req-a", " ", "ref-1"))).Ok);
        // A cycle-keyed run names its collector, and a blank name names none.
        Assert.False((await writes.AppendEvidenceAsync(Run(
            "org-a", "req-a", null, null, collectorId: " ", cycleId: "cycle-1"))).Ok);
        Assert.False((await writes.AppendEvidenceAsync(Run(
            "org-a", "req-a", null, null, collectorId: "coll-a", cycleId: " "))).Ok);

        Assert.Empty(await store.GetEvidenceRunsAsync("org-a", "req-a"));

        // The legacy replay contract is unchanged for a run that carries no cycle.
        Assert.True((await writes.AppendEvidenceAsync(Run("org-a", "req-a", "v", "ref-1"))).Ok);
        Assert.False((await writes.AppendEvidenceAsync(Run("org-a", "req-a", "v", "ref-1"))).Ok);
        Assert.Single(await store.GetEvidenceRunsAsync("org-a", "req-a"));
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task TheClosedResultSetIsCaseSensitiveAndTrailingSpaceSensitive()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        // Written raw, because the store rejects each of these before any SQL runs. Each row carries a
        // vendor, a collector reference, a null cycle, and a null error detail, so ck_evidence_runs_result
        // is the only constraint it can break - and the constraint the server names is what discriminates.
        // Under a case-insensitive comparison the first row would pass this constraint and break the
        // error-detail one instead; under a PAD SPACE collation the third row would store.
        var n = 0;
        foreach (var badResult in new[] { "error", "Error ", "Fail " })
        {
            var message = await ViolatedCheckAsync(
                conn,
                "INSERT INTO evidence_runs (id, kind, organisation_id, requirement_id, vendor, collector_ref, "
                + "result, collected_at, created_at) "
                + "VALUES (@Id, 'Collector', 'org-a', 'req-a', 'v', @Ref, @Result, @At, @At);",
                new { Id = Id(++n), Ref = $"coll-a:r{n}", Result = badResult, At = DateTime.UtcNow });
            Assert.Contains("ck_evidence_runs_result", message, StringComparison.Ordinal);
        }

        // An empty collector_id names no collector to the read side, so a cycle-keyed row carrying one
        // would store and then be assessed for no collector at all.
        var identity = await ViolatedCheckAsync(
            conn,
            "INSERT INTO evidence_runs (id, kind, organisation_id, requirement_id, vendor, collector_ref, "
            + "result, collected_at, created_at, collector_id, cycle_id) "
            + "VALUES (@Id, 'Collector', 'org-a', 'req-a', NULL, NULL, 'Pass', @At, @At, '', 'cycle-1');",
            new { Id = Id(++n), At = DateTime.UtcNow });
        Assert.Contains("ck_evidence_runs_cycle_identity", identity, StringComparison.Ordinal);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task ACycleIsAssessedAsOneOutcomeAcrossItsMachines()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        var writes = new MySqlEvidenceWriteStore(db.ConnectionFactory, new UlidFactory());
        var store = new MySqlEvidenceStore(db.ConnectionFactory);
        var fresh = DateTime.UtcNow;

        // One failing machine among passing siblings decides the cycle.
        await SeedCycleAsync(writes, "req-fail", "coll-fail", "cycle-f", fresh,
            [("machine-1", "Pass", "Pass"), ("machine-2", "Fail", "Pass"), ("machine-3", "Pass", "Pass")]);
        // One errored machine among passing siblings does too.
        await SeedCycleAsync(writes, "req-error", "coll-error", "cycle-e", fresh,
            [("machine-1", "Pass", "Pass"), ("machine-2", "Pass", "Error"), ("machine-3", "Pass", "Pass")]);
        // A machine that has left the fleet appears only in the older cycle, so it stops contributing.
        await SeedCycleAsync(writes, "req-retired", "coll-retired", "cycle-old", fresh.AddHours(-2),
            [("machine-1", "Pass", "Pass"), ("machine-gone", "Fail", "Pass")]);
        await SeedCycleAsync(writes, "req-retired", "coll-retired", "cycle-new", fresh,
            [("machine-1", "Pass", "Pass")]);

        var results = (await store.GetCollectorEvidenceStatusesAsync(["org-a"]))
            .ToDictionary(r => r.CollectorId, r => r.Status, StringComparer.Ordinal);

        Assert.Equal("HardFailure", results["coll-fail"]);
        Assert.Equal("Errored", results["coll-error"]);
        Assert.Equal("Passing", results["coll-retired"]);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task ErroredSitsBelowHardFailureAndAboveStale()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        var writes = new MySqlEvidenceWriteStore(db.ConnectionFactory, new UlidFactory());
        var store = new MySqlEvidenceStore(db.ConnectionFactory);
        var fresh = DateTime.UtcNow;
        var overdue = DateTime.UtcNow.AddDays(-2); // the daily window plus grace is 30h

        // A fresh errored run is Errored, not Passing and not Stale.
        Assert.True((await writes.AppendEvidenceAsync(Run(
            "org-a", "req-error", "v", "coll-e:r1", "Error", collectorId: "coll-e", frequency: "daily",
            collectedAt: fresh, errorDetail: "provider rejected the credential"))).Ok);
        // An observed hard failure outranks an error in the same cycle: red is reserved for the breach.
        await SeedCycleAsync(writes, "req-mixed", "coll-mixed", "cycle-m", fresh,
            [("machine-1", "Fail", "Pass"), ("machine-2", "Pass", "Error")]);
        // The checks an errored run DID observe keep their full weight.
        Assert.True((await writes.AppendEvidenceAsync(Run(
            "org-a", "req-partial", "v", "coll-p:r1", "Error", collectorId: "coll-p", frequency: "daily",
            collectedAt: fresh, errorDetail: "provider closed the connection",
            checks: [Check("h", "Hard", "Fail")]))).Ok);
        // "The collection attempt failed" is sharper and fresher than "the last collection is overdue".
        Assert.True((await writes.AppendEvidenceAsync(Run(
            "org-a", "req-old", "v", "coll-o:r1", "Error", collectorId: "coll-o", frequency: "daily",
            collectedAt: overdue, errorDetail: "provider unreachable"))).Ok);

        var results = (await store.GetCollectorEvidenceStatusesAsync(["org-a"]))
            .ToDictionary(r => r.CollectorId, r => r.Status, StringComparer.Ordinal);

        Assert.Equal("Errored", results["coll-e"]);
        Assert.Equal("HardFailure", results["coll-mixed"]);
        Assert.Equal("HardFailure", results["coll-p"]);
        Assert.Equal("Errored", results["coll-o"]);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task ARunWithNoCycleIsAssessedAlone()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        var writes = new MySqlEvidenceWriteStore(db.ConnectionFactory, new UlidFactory());
        var store = new MySqlEvidenceStore(db.ConnectionFactory);
        var now = DateTime.UtcNow;

        // Neither run carries a cycle, so the assessed set is the pinned run alone and the earlier hard
        // failure does not reach the status - the behaviour a pre-migration collector already had.
        Assert.True((await writes.AppendEvidenceAsync(Run(
            "org-a", "req-a", "v", "coll-a:r1", "Fail", collectorId: "coll-a", frequency: "daily",
            collectedAt: now.AddHours(-2), checks: [Check("h", "Hard", "Fail")]))).Ok);
        Assert.True((await writes.AppendEvidenceAsync(Run(
            "org-a", "req-a", "v", "coll-a:r2", collectorId: "coll-a", frequency: "daily",
            collectedAt: now, checks: [Check("h", "Hard", "Pass")]))).Ok);

        var row = Assert.Single(await store.GetCollectorEvidenceStatusesAsync(["org-a"]));
        Assert.Equal("Passing", row.Status);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task AnEmptyCollectorIdFallsBackToTheCollectorRefPrefix()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        // An empty collector_id is absence, not an identity. A COALESCE-shaped expression would read it as
        // present and group the run under a collector named by the empty string.
        await conn.ExecuteAsync(
            "INSERT INTO evidence_runs (id, kind, organisation_id, requirement_id, vendor, collector_ref, "
            + "result, collected_at, created_at, collector_id) "
            + "VALUES (@Id, 'Collector', 'org-a', 'req-a', 'v', 'legacy-coll:run1', 'Pass', @At, @At, '');",
            new { Id = Id(1), At = DateTime.UtcNow });

        var store = new MySqlEvidenceStore(db.ConnectionFactory);
        var row = Assert.Single(await store.GetCollectorEvidenceStatusesAsync(["org-a"]));
        Assert.Equal("legacy-coll", row.CollectorId);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task TheDerivationComparesUnderABinaryNoPadCollation()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        var writes = new MySqlEvidenceWriteStore(db.ConnectionFactory, new UlidFactory());
        var store = new MySqlEvidenceStore(db.ConnectionFactory);
        var fresh = DateTime.UtcNow;

        Assert.True((await writes.AppendEvidenceAsync(Run(
            "org-a", "req-a", "v", "coll-a:r1", collectorId: "coll-a", frequency: "daily",
            collectedAt: fresh, checks: [Check("h", "Hard", "Pass")]))).Ok);

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var runId = await conn.ExecuteScalarAsync<string>(
            "SELECT id FROM evidence_runs WHERE collector_id = 'coll-a';");

        // Written raw, because the store rejects both check rows. The columns inherit the server's
        // case-insensitive default collation, so only the explicit binary NO PAD collation on each compared
        // literal keeps the SQL accepting exactly what the ordinal comparison accepts.
        await conn.ExecuteAsync(
            "INSERT INTO evidence_checks (id, evidence_id, name, severity, result, ordinal, detail) "
            + "VALUES (@Id, @Run, 'lowercase', 'hard', 'fail', 1, NULL);",
            new { Id = Id(1), Run = runId });
        await conn.ExecuteAsync(
            "INSERT INTO evidence_checks (id, evidence_id, name, severity, result, ordinal, detail) "
            + "VALUES (@Id, @Run, 'padded', 'Hard ', 'Fail ', 2, NULL);",
            new { Id = Id(2), Run = runId });

        var row = Assert.Single(await store.GetCollectorEvidenceStatusesAsync(["org-a"]));
        Assert.Equal("Passing", row.Status);

        // The kind filter is case-sensitive the same way, and the store writes only the PascalCase name.
        await conn.ExecuteAsync(
            "INSERT INTO evidence_runs (id, kind, organisation_id, requirement_id, vendor, collector_ref, "
            + "result, collected_at, created_at, collector_id) "
            + "VALUES (@Id, 'collector', 'org-a', 'req-b', 'v', 'coll-b:r1', 'Pass', @At, @At, 'coll-b');",
            new { Id = Id(3), At = fresh });

        var statuses = await store.GetCollectorEvidenceStatusesAsync(["org-a"]);
        Assert.DoesNotContain(statuses, s => s.CollectorId == "coll-b");
    }

    // Seeds one collection cycle: one run per machine, each with a Hard check and a run-overall result.
    private static async Task SeedCycleAsync(
        MySqlEvidenceWriteStore writes,
        string requirementId,
        string collectorId,
        string cycleId,
        DateTime collectedAt,
        IReadOnlyList<(string Asset, string HardCheck, string Result)> machines)
    {
        foreach (var (asset, hardCheck, result) in machines)
        {
            var errored = string.Equals(result, "Error", StringComparison.Ordinal);
            var appended = await writes.AppendEvidenceAsync(Run(
                "org-a", requirementId, null, null, result, collectorId: collectorId, frequency: "daily",
                collectedAt: collectedAt, assetId: asset, cycleId: cycleId,
                errorDetail: errored ? "provider unreachable" : null,
                checks: errored ? [] : [Check("h", "Hard", hardCheck)]));
            Assert.True(appended.Ok, appended.Error);
        }
    }

    // Runs a statement expected to break a check constraint and returns the server's message, which names
    // the constraint. The name is what tells one producer bug from another.
    private static async Task<string> ViolatedCheckAsync(MySqlConnection conn, string sql, object args)
    {
        var ex = await Assert.ThrowsAsync<MySqlException>(() => conn.ExecuteAsync(sql, args));
        Assert.Equal(3819, (int)ex.ErrorCode);
        return ex.Message;
    }
}
