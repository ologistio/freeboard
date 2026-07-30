using Dapper;
using Freeboard.Persistence;
using Freeboard.Persistence.Auth;
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

    // Seeds one discovered machine through the ingest write path - the only writer of a discovered asset -
    // and returns the id it allocated. A discovered id is a generated ULID, so a test that needs to collide
    // with it has to author its config after the seed.
    private static async Task<string> SeedDiscoveredMachineAsync(MySqlTestDatabase db)
    {
        var writes = new MySqlAssetWriteStore(db.ConnectionFactory, new UlidFactory());
        var result = await writes.UpsertMachineFromSourceAsync(
            new NewMachineObservation("org-a", "fleet", "host-1", "SN-1", null, null));

        Assert.Equal(AssetUpsertStatus.Created, result.Status);
        return result.AssetId!;
    }

    private static async Task<long> TableCountAsync(MySqlTestDatabase db)
    {
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE();");
    }

    // Sync a valid config WITHOUT --migrate on a truly empty DB -> exit 3 AND no tables.
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

    // sync --migrate against a truly empty database bootstraps the schema and imports in one command:
    // exit 0, the migration-tracking table and every table the persisted kinds need exist, and each
    // kind's rows land.
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
                     "collectors", "integration_connections",
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
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM collectors;"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM integration_connections;"));
    }

    // An applied migration whose checksum no longer matches -> sync exits 3, imports nothing.
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

    // schema_migrations records a version with no embedded migration -> sync exits 3, imports nothing.
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

    // A full config persists the new-kind rows and round-trips their field values; re-syncing a config
    // that drops one scope, three collectors, and one integration connection hard-removes exactly those
    // rows while keeping their FK targets (the vendor subject, control, and requirement they referenced)
    // and the other retained rows. Dropping a collector together with the connection it referenced also
    // pins the prune order: the wrong order raises the connection's RESTRICT foreign key. Three removal
    // paths run here - the whole-set scope replace, the DeleteAbsent collector prune, and the DeleteAbsent
    // connection prune.
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

            Assert.Equal(2, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM scopes;"));
            Assert.Equal(5, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM collectors;"));
            Assert.Equal(2, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM integration_connections;"));

            // Read back a persisted field value on a retained row to prove a true round-trip, not just
            // row identity: ec-keep's type must equal the config's 'integration'.
            Assert.Equal("integration", await conn.ExecuteScalarAsync<string>(
                "SELECT type FROM collectors WHERE id = 'ec-keep';"));
            // And its config round-trips through the merged column under the stored member name.
            Assert.Equal("keep-check", await conn.ExecuteScalarAsync<string>(
                "SELECT config->>'$.Checks[0].Name' FROM collectors WHERE id = 'ec-keep';"));

            // The same round-trip readback for the retained connection, its optional vendor included.
            Assert.Equal("fleet", await conn.ExecuteScalarAsync<string>(
                "SELECT provider FROM integration_connections WHERE id = 'conn-a';"));
            Assert.Equal("https://fleet.example.com", await conn.ExecuteScalarAsync<string>(
                "SELECT base_url FROM integration_connections WHERE id = 'conn-a';"));
            Assert.Equal("daily", await conn.ExecuteScalarAsync<string>(
                "SELECT discovery_cadence FROM integration_connections WHERE id = 'conn-a';"));
            Assert.Equal("vendor-a", await conn.ExecuteScalarAsync<string>(
                "SELECT vendor_id FROM integration_connections WHERE id = 'conn-a';"));

            // No token value is stored because the table carries no token column by construction: the API
            // token is resolved out-of-band by connection id.
            Assert.Equal(0, await conn.ExecuteScalarAsync<long>(
                """
                SELECT COUNT(*) FROM information_schema.columns
                WHERE table_schema = DATABASE() AND table_name = 'integration_connections'
                  AND column_name LIKE '%token%';
                """));

            var (dropExit, _, _) = Capture(() => new GitOpsCommands().Sync(dropped));
            Assert.Equal(0, dropExit);

            // The dropped rows are gone; the retained ones remain.
            Assert.Equal(1, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM scopes;"));
            Assert.Equal(2, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM collectors;"));
            Assert.Equal(1, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM integration_connections;"));
            Assert.Equal(0, await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM scopes WHERE id = 'vs-drop';"));
            Assert.Equal(0, await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM collectors WHERE id IN ('ec-drop', 'at-drop', 'ec-conn-drop');"));
            Assert.Equal(0, await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM integration_connections WHERE id = 'conn-drop';"));
            Assert.Equal(1, await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM scopes WHERE id = 'vs-keep';"));
            Assert.Equal(2, await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM collectors WHERE id IN ('ec-keep', 'at-keep');"));
            Assert.Equal(1, await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM integration_connections WHERE id = 'conn-a';"));

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

    // A config that declares no Machine assets is not an error and not a removal instruction: sync exits 0
    // and the discovered machine is still there afterwards. The exit code is the point - the row-level
    // survival matrix belongs to the persistence suite, which owns the store guarantee.
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task SyncDeclaringNoMachinesExitsZeroAndLeavesTheDiscoveredMachine()
    {
        await using var db = await RequireDbAsync();
        await new MySqlMigrationRunner(db.ConnectionFactory).ApplyPendingAsync();
        Environment.SetEnvironmentVariable("FREEBOARD_DB", db.ConnectionString);
        var discoveredId = await SeedDiscoveredMachineAsync(db);

        var dir = WriteTempConfig(DeclaredOrgOnlyConfig);
        try
        {
            var (exit, _, _) = Capture(() => new GitOpsCommands().Sync(dir));

            Assert.Equal(0, exit);

            await using var conn = new MySqlConnection(db.ConnectionString);
            await conn.OpenAsync();
            Assert.Equal("discovered", await conn.ExecuteScalarAsync<string>(
                "SELECT source FROM assets WHERE id = @Id;", new { Id = discoveredId }));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // The case an operator most fears: a config emptied of assets against a store holding both declared and
    // discovered rows. Sync still exits 0, the declared rows it previously wrote are hard-removed, and the
    // discovered machine is untouched.
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task SyncOfAZeroAssetConfigExitsZeroRemovingDeclaredAndKeepingDiscovered()
    {
        await using var db = await RequireDbAsync();
        await new MySqlMigrationRunner(db.ConnectionFactory).ApplyPendingAsync();
        Environment.SetEnvironmentVariable("FREEBOARD_DB", db.ConnectionString);
        var discoveredId = await SeedDiscoveredMachineAsync(db);

        var withAssets = WriteTempConfig(DeclaredOrgOnlyConfig);
        var withoutAssets = WriteTempConfig(NoAssetConfig);
        try
        {
            Assert.Equal(0, Capture(() => new GitOpsCommands().Sync(withAssets)).Exit);

            var (exit, _, _) = Capture(() => new GitOpsCommands().Sync(withoutAssets));

            Assert.Equal(0, exit);

            await using var conn = new MySqlConnection(db.ConnectionString);
            await conn.OpenAsync();
            Assert.Equal(0, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM assets WHERE id = 'org-a';"));
            Assert.Equal("discovered", await conn.ExecuteScalarAsync<string>(
                "SELECT source FROM assets WHERE id = @Id;", new { Id = discoveredId }));
        }
        finally
        {
            Directory.Delete(withAssets, recursive: true);
            Directory.Delete(withoutAssets, recursive: true);
        }
    }

    // The collision is detected inside the import transaction against database state, which config
    // validation cannot see, so the command maps it to 3 (operational) rather than 1 (validation) and its
    // message names the id. The other declared asset in the same config proves the command aborted instead
    // of partially applying; the whole-store no-mutation guarantee is the persistence suite's.
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task SyncOfADeclaredIdCollidingWithADiscoveredIdExitsThreeNamingTheId()
    {
        await using var db = await RequireDbAsync();
        await new MySqlMigrationRunner(db.ConnectionFactory).ApplyPendingAsync();
        Environment.SetEnvironmentVariable("FREEBOARD_DB", db.ConnectionString);
        var discoveredId = await SeedDiscoveredMachineAsync(db);

        var dir = WriteTempConfig($"""
            apiVersion: freeboard.dev/v1alpha1
            kind: Asset
            id: org-a
            title: Org A
            type: Company
            source: declared
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: Asset
            id: {discoveredId}
            title: Colliding company
            type: Company
            source: declared
            """);
        try
        {
            var (exit, _, err) = Capture(() => new GitOpsCommands().Sync(dir));

            Assert.Equal(3, exit);
            Assert.Contains(discoveredId, err, StringComparison.Ordinal);
            Assert.Contains("Nothing was written.", err, StringComparison.Ordinal);

            await using var conn = new MySqlConnection(db.ConnectionString);
            await conn.OpenAsync();
            Assert.Equal(0, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM assets WHERE id = 'org-a';"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // A declared Company and nothing else: the config declares no Machine assets at all.
    private const string DeclaredOrgOnlyConfig = """
        apiVersion: freeboard.dev/v1alpha1
        kind: Asset
        id: org-a
        title: Org A
        type: Company
        source: declared
        """;

    // A valid config carrying no Asset document of any type.
    private const string NoAssetConfig = """
        apiVersion: freeboard.dev/v1alpha1
        kind: Standard
        id: std-a
        title: Standard A
        version: "1.0"
        authority: Example Authority
        """;

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

    // Standard/requirement/control plus a vendor, two vendor-subject scopes, two integration connections
    // (conn-a naming the vendor, conn-drop not), and five collectors - three data sources and two
    // attestations. ctrl-a declares evaluation because it has attached collectors.
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
        kind: Integration
        id: conn-a
        title: Connection A
        provider: fleet
        base_url: https://fleet.example.com
        discovery_cadence: daily
        vendor: vendor-a
        ---
        apiVersion: freeboard.dev/v1alpha1
        kind: Integration
        id: conn-drop
        title: Connection to drop
        provider: fleet
        base_url: https://drop.fleet.example.com
        discovery_cadence: weekly
        ---
        apiVersion: freeboard.dev/v1alpha1
        kind: Scope
        id: vs-keep
        title: Keep scope
        subject: vendor-a
        control: ctrl-a
        disposition: In
        ---
        apiVersion: freeboard.dev/v1alpha1
        kind: Scope
        id: vs-drop
        title: Drop scope
        subject: vendor-a
        requirement: req-a
        disposition: In
        ---
        apiVersion: freeboard.dev/v1alpha1
        kind: Collector
        id: ec-keep
        title: Keep collector
        control: ctrl-a
        vendor: vendor-a
        type: integration
        provider: fleet
        frequency: daily
        connection: conn-a
        config:
          checks:
            - source_key: "1"
              name: keep-check
              severity: Hard
        ---
        apiVersion: freeboard.dev/v1alpha1
        kind: Collector
        id: ec-drop
        title: Drop collector
        control: ctrl-a
        vendor: vendor-a
        type: script
        frequency: weekly
        ---
        apiVersion: freeboard.dev/v1alpha1
        kind: Collector
        id: ec-conn-drop
        title: Drop collector on the dropped connection
        control: ctrl-a
        type: integration
        provider: fleet
        frequency: weekly
        connection: conn-drop
        config:
          checks:
            - source_key: "2"
              name: drop-check
              severity: Soft
        ---
        apiVersion: freeboard.dev/v1alpha1
        kind: Collector
        id: at-keep
        title: Keep attestation
        control: ctrl-a
        type: manual
        frequency: annual
        ---
        apiVersion: freeboard.dev/v1alpha1
        kind: Collector
        id: at-drop
        title: Drop attestation
        control: ctrl-a
        type: manual
        frequency: annual
        """;

    // The full config narrowed: vs-drop, ec-drop, at-drop, ec-conn-drop, and conn-drop are all omitted.
    // The FK targets of the dropped scope and collectors are retained (vendor-a, ctrl-a, req-a), but
    // conn-drop is ec-conn-drop's FK target and is deliberately dropped with it - the case that pins the
    // collector-before-connection prune order.
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
        kind: Integration
        id: conn-a
        title: Connection A
        provider: fleet
        base_url: https://fleet.example.com
        discovery_cadence: daily
        vendor: vendor-a
        ---
        apiVersion: freeboard.dev/v1alpha1
        kind: Scope
        id: vs-keep
        title: Keep scope
        subject: vendor-a
        control: ctrl-a
        disposition: In
        ---
        apiVersion: freeboard.dev/v1alpha1
        kind: Collector
        id: ec-keep
        title: Keep collector
        control: ctrl-a
        vendor: vendor-a
        type: integration
        provider: fleet
        frequency: daily
        connection: conn-a
        config:
          checks:
            - source_key: "1"
              name: keep-check
              severity: Hard
        ---
        apiVersion: freeboard.dev/v1alpha1
        kind: Collector
        id: at-keep
        title: Keep attestation
        control: ctrl-a
        type: manual
        frequency: annual
        """;
}
