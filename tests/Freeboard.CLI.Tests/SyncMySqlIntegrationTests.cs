using Dapper;
using Freeboard.Persistence.System;
using Freeboard.TestInfrastructure;
using MySqlConnector;

namespace Freeboard.CLI.Tests;

/// <summary>
/// End-to-end tests of the REAL GitOpsCommands.Sync path against a real MySQL discovered
/// via FREEBOARD_TEST_DB. They run with the default PersistenceFactory (real importer and
/// migration runner), so they exercise the migrate-first gate, the integrity gate, and the
/// import for real. Each SKIPS cleanly (not fails) when no test DB is configured. Each gets
/// a fresh throwaway database. Serialized with the other persistence-cli tests because
/// PersistenceFactory and Console are process-global.
/// </summary>
[Trait("Category", TestCategories.Integration)]
[Collection("persistence-cli")]
public sealed class SyncMySqlIntegrationTests : IDisposable
{
    private readonly Func<string, Freeboard.Persistence.GitOps.IGitOpsImporter> originalImporter =
        PersistenceFactory.CreateImporter;

    private readonly Func<string, IMigrationRunner> originalRunner = PersistenceFactory.CreateMigrationRunner;
    private readonly string? originalEnv = Environment.GetEnvironmentVariable("FREEBOARD_DB");
    private readonly TextWriter originalOut = Console.Out;
    private readonly TextWriter originalErr = Console.Error;

    public void Dispose()
    {
        PersistenceFactory.CreateImporter = originalImporter;
        PersistenceFactory.CreateMigrationRunner = originalRunner;
        Environment.SetEnvironmentVariable("FREEBOARD_DB", originalEnv);
        Console.SetOut(originalOut);
        Console.SetError(originalErr);
    }

    // Path.Join (not Path.Combine) so a rooted name cannot silently drop the base path.
    private static string FixtureDir(string name) => Path.Join(AppContext.BaseDirectory, "fixtures", name);

    private static string WriteTempConfig(string content)
    {
        var dir = Directory.CreateTempSubdirectory("fb-gitops-sync-");
        File.WriteAllText(Path.Join(dir.FullName, "config.yaml"), content);
        return dir.FullName;
    }

    private static async Task<MySqlTestDatabase> RequireDbAsync()
    {
        var db = await MySqlTestDatabase.TryCreateAsync();
        Skip.If(db is null, $"{MySqlTestDatabase.EnvVar} not set; skipping MySQL Sync integration test.");
        return db!;
    }

    private static (int Exit, string Out, string Err) Capture(Func<int> run)
    {
        using var outW = new StringWriter();
        using var errW = new StringWriter();
        Console.SetOut(outW);
        Console.SetError(errW);
        var exit = run();
        return (exit, outW.ToString(), errW.ToString());
    }

    private static async Task<long> TableCountAsync(MySqlTestDatabase db)
    {
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE();");
    }

    // IF-3 (a): sync a valid config WITHOUT --migrate on a truly empty DB -> exit 3 AND no tables.
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task SyncWithoutMigrateOnEmptyDbExitsThreeAndCreatesNoTables()
    {
        await using var db = await RequireDbAsync();
        Environment.SetEnvironmentVariable("FREEBOARD_DB", db.ConnectionString);

        var (exit, _, err) = Capture(() => new GitOpsCommands().Sync(FixtureDir("valid")));

        Assert.Equal(3, exit);
        Assert.Contains("--migrate", err);
        Assert.Equal(0, await TableCountAsync(db));
    }

    // IF-3 (b): sync --migrate on an empty DB -> exit 0, schema_migrations + six tables, data imported.
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task SyncWithMigrateOnEmptyDbBootstrapsMigratesImportsExitsZero()
    {
        await using var db = await RequireDbAsync();
        Environment.SetEnvironmentVariable("FREEBOARD_DB", db.ConnectionString);

        var (exit, _, _) = Capture(() => new GitOpsCommands().Sync(FixtureDir("valid"), migrate: true));

        Assert.Equal(0, exit);

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var tables = (await conn.QueryAsync<string>(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = DATABASE();"))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var t in new[]
                 {
                     "standards", "requirements", "controls", "assets", "scopes",
                     "control_requirements", "schema_migrations",
                 })
        {
            Assert.Contains(t, tables);
        }

        Assert.Equal(1, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM standards;"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM requirements;"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM controls;"));
        Assert.Equal(2, await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM assets WHERE type IN ('Company', 'Department');"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM scopes;"));
    }

    // IF-1 (a): an applied migration whose checksum no longer matches -> sync exits 3, imports nothing.
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task SyncOnChecksumMismatchExitsThreeAndImportsNothing()
    {
        await using var db = await RequireDbAsync();
        await new MySqlMigrationRunner(db.ConnectionFactory).ApplyPendingAsync();
        Environment.SetEnvironmentVariable("FREEBOARD_DB", db.ConnectionString);

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            "UPDATE schema_migrations SET checksum = REPEAT('0', 64) WHERE version = '001_initial_schema';");

        var (exit, _, err) = Capture(() => new GitOpsCommands().Sync(FixtureDir("valid")));

        Assert.Equal(3, exit);
        Assert.Contains("checksum", err, StringComparison.OrdinalIgnoreCase);
        // Imported nothing despite the schema being otherwise current.
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM standards;"));
    }

    // IF-1 (b): schema_migrations records a version with no embedded migration -> sync exits 3, imports nothing.
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task SyncOnRecordedButMissingMigrationExitsThreeAndImportsNothing()
    {
        await using var db = await RequireDbAsync();
        await new MySqlMigrationRunner(db.ConnectionFactory).ApplyPendingAsync();
        Environment.SetEnvironmentVariable("FREEBOARD_DB", db.ConnectionString);

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            "INSERT INTO schema_migrations (version, checksum, applied_at) VALUES ('999_gone', 'x', NOW(6));");

        var (exit, _, err) = Capture(() => new GitOpsCommands().Sync(FixtureDir("valid")));

        Assert.Equal(3, exit);
        Assert.Contains("missing", err, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM standards;"));
    }

    // A full config persists the new-kind rows; re-syncing a config that drops one vendor-scope, one
    // evidence-collector, and one attestation-template hard-removes exactly those rows while keeping their
    // FK targets (the vendor, control, and requirement they referenced) and the other retained rows. This
    // covers the "drop only the resource, keep its FK target" case at the command surface and exercises both
    // removal paths: the whole-set ReplaceVendorScopes and the DeleteAbsent collector/template prunes.
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task SyncRoundTripThenDropRemovesDroppedNewKindRowsKeepingTargets()
    {
        await using var db = await RequireDbAsync();
        Environment.SetEnvironmentVariable("FREEBOARD_DB", db.ConnectionString);

        var full = WriteTempConfig(FullConfig);
        var dropped = WriteTempConfig(DroppedConfig);
        try
        {
            var (fullExit, _, _) = Capture(() => new GitOpsCommands().Sync(full, migrate: true));
            Assert.Equal(0, fullExit);

            await using var conn = new MySqlConnection(db.ConnectionString);
            await conn.OpenAsync();

            Assert.Equal(2, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM vendor_scopes;"));
            Assert.Equal(2, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM evidence_collectors;"));
            Assert.Equal(2, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM attestation_templates;"));

            // Read back a persisted field value on a retained row to prove a true round-trip, not just
            // row identity: ec-keep's type must equal the config's 'integration'.
            Assert.Equal("integration", await conn.ExecuteScalarAsync<string>(
                "SELECT type FROM evidence_collectors WHERE id = 'ec-keep';"));

            var (dropExit, _, _) = Capture(() => new GitOpsCommands().Sync(dropped));
            Assert.Equal(0, dropExit);

            // The dropped rows are gone; the retained ones remain.
            Assert.Equal(1, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM vendor_scopes;"));
            Assert.Equal(1, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM evidence_collectors;"));
            Assert.Equal(1, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM attestation_templates;"));
            Assert.Equal(0, await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM vendor_scopes WHERE id = 'vs-drop';"));
            Assert.Equal(0, await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM evidence_collectors WHERE id = 'ec-drop';"));
            Assert.Equal(0, await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM attestation_templates WHERE id = 'at-drop';"));
            Assert.Equal(1, await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM vendor_scopes WHERE id = 'vs-keep';"));
            Assert.Equal(1, await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM evidence_collectors WHERE id = 'ec-keep';"));
            Assert.Equal(1, await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM attestation_templates WHERE id = 'at-keep';"));

            // The FK targets of the dropped rows survive: the vendor, control, and requirement are kept.
            Assert.Equal(1, await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM assets WHERE id = 'vendor-a' AND type = 'Vendor';"));
            Assert.Equal(1, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM controls WHERE id = 'ctrl-a';"));
            Assert.Equal(1, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM requirements WHERE id = 'req-a';"));
        }
        finally
        {
            Directory.Delete(full, recursive: true);
            Directory.Delete(dropped, recursive: true);
        }
    }

    // A non-blocking validation warning (an ownerless declared Vendor) must not fail sync: the command
    // still exits 0, imports the config, and prints the warning to stderr so the operator sees it. This
    // proves the success-path warning surface for sync (validate and apply --dry-run are covered by the
    // in-memory GitOpsCommandTests; sync needs a real DB).
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task SyncPrintsNonBlockingWarningToStderrAndExitsZero()
    {
        await using var db = await RequireDbAsync();
        Environment.SetEnvironmentVariable("FREEBOARD_DB", db.ConnectionString);

        var dir = WriteTempConfig(OwnerlessVendorConfig);
        try
        {
            var (exit, _, err) = Capture(() => new GitOpsCommands().Sync(dir, migrate: true));

            Assert.Equal(0, exit);
            Assert.Contains("vendor-x", err, StringComparison.Ordinal);
            Assert.Contains("no owner", err, StringComparison.Ordinal);

            await using var conn = new MySqlConnection(db.ConnectionString);
            await conn.OpenAsync();
            // The warning did not block the import: the ownerless vendor was still synced.
            Assert.Equal(1, await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM assets WHERE id = 'vendor-x' AND type = 'Vendor';"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // A Company root and a declared Vendor with no owner. The ownerless vendor is a non-blocking warning
    // (it is visible to no caller until an owner is set), so sync succeeds and the warning reaches stderr.
    private const string OwnerlessVendorConfig = """
        apiVersion: freeboard.dev/v1alpha1
        kind: Asset
        id: org-root
        title: Root Co
        type: Company
        source: declared
        ---
        apiVersion: freeboard.dev/v1alpha1
        kind: Asset
        id: vendor-x
        title: Vendor X
        type: Vendor
        source: declared
        """;

    // Standard/requirement/control plus a vendor, two vendor-scopes, two evidence-collectors, and two
    // attestation-templates. ctrl-a declares evaluation because it has attached collectors.
    private const string FullConfig = """
        apiVersion: freeboard.dev/v1alpha1
        kind: Standard
        id: std-a
        title: Standard A
        version: "1.0"
        authority: Example Authority
        ---
        apiVersion: freeboard.dev/v1alpha1
        kind: Requirement
        id: req-a
        title: Requirement A
        standard: std-a
        theme: Theme A
        statement: Do the thing.
        citation_label: Source A
        citation_url: https://example.com/a
        ---
        apiVersion: freeboard.dev/v1alpha1
        kind: Control
        id: ctrl-a
        title: Control A
        maps_to:
          - req-a
        evaluation: all
        ---
        apiVersion: freeboard.dev/v1alpha1
        kind: Asset
        id: vendor-a
        title: Vendor A
        type: Vendor
        source: declared
        ---
        apiVersion: freeboard.dev/v1alpha1
        kind: IntegrationConnection
        id: conn-a
        title: Connection A
        provider: fleet
        base_url: https://fleet.example.com
        discovery_cadence: daily
        ---
        apiVersion: freeboard.dev/v1alpha1
        kind: VendorScope
        id: vs-keep
        title: Keep scope
        vendor: vendor-a
        control: ctrl-a
        disposition: In
        ---
        apiVersion: freeboard.dev/v1alpha1
        kind: VendorScope
        id: vs-drop
        title: Drop scope
        vendor: vendor-a
        requirement: req-a
        disposition: In
        ---
        apiVersion: freeboard.dev/v1alpha1
        kind: EvidenceCollector
        id: ec-keep
        title: Keep collector
        control: ctrl-a
        vendor: vendor-a
        type: integration
        frequency: daily
        connection: conn-a
        checks:
          - source_key: "1"
            name: keep-check
            severity: Hard
        ---
        apiVersion: freeboard.dev/v1alpha1
        kind: EvidenceCollector
        id: ec-drop
        title: Drop collector
        control: ctrl-a
        vendor: vendor-a
        type: script
        frequency: weekly
        ---
        apiVersion: freeboard.dev/v1alpha1
        kind: AttestationTemplate
        id: at-keep
        title: Keep template
        control: ctrl-a
        type: manual
        ---
        apiVersion: freeboard.dev/v1alpha1
        kind: AttestationTemplate
        id: at-drop
        title: Drop template
        control: ctrl-a
        type: manual
        """;

    // The full config with vs-drop, ec-drop, and at-drop removed; every FK target is retained.
    private const string DroppedConfig = """
        apiVersion: freeboard.dev/v1alpha1
        kind: Standard
        id: std-a
        title: Standard A
        version: "1.0"
        authority: Example Authority
        ---
        apiVersion: freeboard.dev/v1alpha1
        kind: Requirement
        id: req-a
        title: Requirement A
        standard: std-a
        theme: Theme A
        statement: Do the thing.
        citation_label: Source A
        citation_url: https://example.com/a
        ---
        apiVersion: freeboard.dev/v1alpha1
        kind: Control
        id: ctrl-a
        title: Control A
        maps_to:
          - req-a
        evaluation: all
        ---
        apiVersion: freeboard.dev/v1alpha1
        kind: Asset
        id: vendor-a
        title: Vendor A
        type: Vendor
        source: declared
        ---
        apiVersion: freeboard.dev/v1alpha1
        kind: IntegrationConnection
        id: conn-a
        title: Connection A
        provider: fleet
        base_url: https://fleet.example.com
        discovery_cadence: daily
        ---
        apiVersion: freeboard.dev/v1alpha1
        kind: VendorScope
        id: vs-keep
        title: Keep scope
        vendor: vendor-a
        control: ctrl-a
        disposition: In
        ---
        apiVersion: freeboard.dev/v1alpha1
        kind: EvidenceCollector
        id: ec-keep
        title: Keep collector
        control: ctrl-a
        vendor: vendor-a
        type: integration
        frequency: daily
        connection: conn-a
        checks:
          - source_key: "1"
            name: keep-check
            severity: Hard
        ---
        apiVersion: freeboard.dev/v1alpha1
        kind: AttestationTemplate
        id: at-keep
        title: Keep template
        control: ctrl-a
        type: manual
        """;
}
