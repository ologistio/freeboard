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

    private static string FixtureDir(string name)
    {
        Assert.False(Path.IsPathRooted(name), "fixture name must be relative");
        return Path.Combine(AppContext.BaseDirectory, "fixtures", name);
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
    [SkippableFact]
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
    [SkippableFact]
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
                     "standards", "controls", "scopes", "control_standards", "scope_controls", "schema_migrations",
                 })
        {
            Assert.Contains(t, tables);
        }

        Assert.Equal(1, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM standards;"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM controls;"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM scopes;"));
    }

    // IF-1 (a): an applied migration whose checksum no longer matches -> sync exits 3, imports nothing.
    [SkippableFact]
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
    [SkippableFact]
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
}
