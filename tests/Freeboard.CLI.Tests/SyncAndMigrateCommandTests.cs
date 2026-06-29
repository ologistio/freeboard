using Freeboard.Persistence.GitOps;
using Freeboard.Persistence.System;

namespace Freeboard.CLI.Tests;

/// <summary>
/// In-process tests of the persistence-backed commands. They drive GitOpsCommands and
/// SystemCommands directly with importer/runner doubles via the PersistenceFactory
/// seam (InternalsVisibleTo). The child-process CliRunner cannot inject doubles, so
/// these run in-process. Serialized: PersistenceFactory and Console are process-global.
/// </summary>
[Collection("persistence-cli")]
public sealed class SyncAndMigrateCommandTests : IDisposable
{
    private readonly Func<string, IGitOpsImporter> originalImporter = PersistenceFactory.CreateImporter;
    private readonly Func<string, IMigrationRunner> originalRunner = PersistenceFactory.CreateMigrationRunner;
    private readonly string? originalEnv = Environment.GetEnvironmentVariable("FREEBOARD_DB");
    private readonly TextWriter originalOut = Console.Out;
    private readonly TextWriter originalErr = Console.Error;

    // Path.Join (not Path.Combine) so a rooted name cannot silently drop the base path.
    private static string FixtureDir(string name) => Path.Join(AppContext.BaseDirectory, "fixtures", name);

    public void Dispose()
    {
        PersistenceFactory.CreateImporter = originalImporter;
        PersistenceFactory.CreateMigrationRunner = originalRunner;
        Environment.SetEnvironmentVariable("FREEBOARD_DB", originalEnv);
        Console.SetOut(originalOut);
        Console.SetError(originalErr);
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

    [Fact]
    public void SyncInvalidConfigExitsOneAndDoesNotImport()
    {
        var importer = new FakeImporter();
        PersistenceFactory.CreateImporter = _ => importer;
        PersistenceFactory.CreateMigrationRunner = _ => new FakeMigrationRunner();
        Environment.SetEnvironmentVariable("FREEBOARD_DB", "Server=x;Database=y;");

        var (exit, _, err) = Capture(() => new GitOpsCommands().Sync(FixtureDir("invalid")));

        Assert.Equal(1, exit);
        Assert.Contains("std-missing", err);
        Assert.Equal(0, importer.Calls);
    }

    [Fact]
    public void SyncValidConfigImportsOnceAndExitsZero()
    {
        var importer = new FakeImporter();
        PersistenceFactory.CreateImporter = _ => importer;
        PersistenceFactory.CreateMigrationRunner = _ => new FakeMigrationRunner { Current = true };

        var (exit, _, _) = Capture(() => new GitOpsCommands().Sync(FixtureDir("valid"), "Server=x;Database=y;"));

        Assert.Equal(0, exit);
        Assert.Equal(1, importer.Calls);
        Assert.NotNull(importer.LastConfig);
        Assert.Single(importer.LastConfig!.Standards);
    }

    [Fact]
    public void SyncUnmigratedWithoutMigrateExitsThreeAndWritesNothing()
    {
        var importer = new FakeImporter();
        var runner = new FakeMigrationRunner { Current = false };
        PersistenceFactory.CreateImporter = _ => importer;
        PersistenceFactory.CreateMigrationRunner = _ => runner;

        var (exit, _, err) = Capture(() => new GitOpsCommands().Sync(FixtureDir("valid"), "Server=x;Database=y;"));

        Assert.Equal(3, exit);
        // Contract-stable user-facing message; assert verbatim.
        Assert.Contains(
            "Schema out of date; run 'system migrate' or pass --migrate. Nothing was written.", err);
        Assert.Equal(1, runner.StateCalls);
        Assert.Equal(0, runner.ApplyCalls);
        Assert.Equal(0, importer.Calls);
    }

    [Fact]
    public void SyncUnmigratedWithMigrateAppliesThenImports()
    {
        var importer = new FakeImporter();
        var runner = new FakeMigrationRunner { Current = false };
        PersistenceFactory.CreateImporter = _ => importer;
        PersistenceFactory.CreateMigrationRunner = _ => runner;

        var (exit, _, _) = Capture(() => new GitOpsCommands().Sync(FixtureDir("valid"), "Server=x;Database=y;", migrate: true));

        Assert.Equal(0, exit);
        Assert.Equal(1, runner.ApplyCalls);
        Assert.Equal(1, importer.Calls);
    }

    // IF-1: a read-only integrity violation makes sync exit 3 before importing, without --migrate.
    [Fact]
    public void SyncIntegrityViolationExitsThreeWithoutImportingNoMigrate()
    {
        var importer = new FakeImporter();
        var runner = new FakeMigrationRunner
        {
            StateIntegrityError = "Applied migration '001' has a different checksum than its embedded migration.",
        };
        PersistenceFactory.CreateImporter = _ => importer;
        PersistenceFactory.CreateMigrationRunner = _ => runner;

        var (exit, _, err) = Capture(() => new GitOpsCommands().Sync(FixtureDir("valid"), "Server=x;Database=y;"));

        Assert.Equal(3, exit);
        Assert.Contains("checksum", err, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, runner.StateCalls);
        Assert.Equal(0, runner.ApplyCalls);
        Assert.Equal(0, importer.Calls);
    }

    // IF-1: the integrity gate also blocks import when --migrate is passed (never apply over a corrupt schema).
    [Fact]
    public void SyncIntegrityViolationExitsThreeWithoutImportingWithMigrate()
    {
        var importer = new FakeImporter();
        var runner = new FakeMigrationRunner
        {
            StateIntegrityError = "Applied migration '001_initial_schema' embedded migration is missing.",
        };
        PersistenceFactory.CreateImporter = _ => importer;
        PersistenceFactory.CreateMigrationRunner = _ => runner;

        var (exit, _, err) = Capture(() =>
            new GitOpsCommands().Sync(FixtureDir("valid"), "Server=x;Database=y;", migrate: true));

        Assert.Equal(3, exit);
        Assert.Contains("missing", err, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, runner.ApplyCalls);
        Assert.Equal(0, importer.Calls);
    }

    [Fact]
    public void MigrateInvokesRunnerAndExitsZero()
    {
        var runner = new FakeMigrationRunner();
        PersistenceFactory.CreateMigrationRunner = _ => runner;

        var (exit, _, _) = Capture(() => new SystemCommands().Migrate("Server=x;Database=y;"));

        Assert.Equal(0, exit);
        Assert.Equal(1, runner.ApplyCalls);
    }

    [Fact]
    public void SyncWithoutConnectionStringExitsThree()
    {
        Environment.SetEnvironmentVariable("FREEBOARD_DB", null);
        PersistenceFactory.CreateMigrationRunner = _ => new FakeMigrationRunner();

        var (exit, _, err) = Capture(() => new GitOpsCommands().Sync(FixtureDir("valid")));

        Assert.Equal(3, exit);
        Assert.Contains("connection string", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MigrateWithoutConnectionStringExitsThree()
    {
        Environment.SetEnvironmentVariable("FREEBOARD_DB", null);

        var (exit, _, err) = Capture(() => new SystemCommands().Migrate());

        Assert.Equal(3, exit);
        Assert.Contains("connection string", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MigrateChecksumMismatchExitsThree()
    {
        PersistenceFactory.CreateMigrationRunner = _ => new FakeMigrationRunner
        {
            ThrowOnApply = new MigrationException("Applied migration '001' has a different checksum"),
        };

        var (exit, _, err) = Capture(() => new SystemCommands().Migrate("Server=x;"));

        Assert.Equal(3, exit);
        Assert.Contains("checksum", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MigrateMissingMigrationExitsThree()
    {
        PersistenceFactory.CreateMigrationRunner = _ => new FakeMigrationRunner
        {
            ThrowOnApply = new MigrationException("Applied migration '001' embedded migration is missing"),
        };

        var (exit, _, err) = Capture(() => new SystemCommands().Migrate("Server=x;"));

        Assert.Equal(3, exit);
        Assert.Contains("missing", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MigrateExecutionFailureExitsThree()
    {
        PersistenceFactory.CreateMigrationRunner = _ => new FakeMigrationRunner
        {
            ThrowOnApply = new MigrationException("Migration '020_broken' failed during execution"),
        };

        var (exit, _, err) = Capture(() => new SystemCommands().Migrate("Server=x;"));

        Assert.Equal(3, exit);
        Assert.Contains("failed", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExplicitConnectionStringOverridesEnvVar()
    {
        Environment.SetEnvironmentVariable("FREEBOARD_DB", "Server=env;");
        string? seen = null;
        PersistenceFactory.CreateMigrationRunner = cs => { seen = cs; return new FakeMigrationRunner(); };

        Capture(() => new SystemCommands().Migrate("Server=explicit;"));

        Assert.Equal("Server=explicit;", seen);
    }

    [Fact]
    public void EnvVarUsedWhenNoOption()
    {
        Environment.SetEnvironmentVariable("FREEBOARD_DB", "Server=env;");
        string? seen = null;
        PersistenceFactory.CreateMigrationRunner = cs => { seen = cs; return new FakeMigrationRunner(); };

        Capture(() => new SystemCommands().Migrate());

        Assert.Equal("Server=env;", seen);
    }
}

[CollectionDefinition("persistence-cli", DisableParallelization = true)]
public sealed class PersistenceCliCollection;
