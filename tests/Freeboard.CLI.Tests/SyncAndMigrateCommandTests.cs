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

        var (exit, output, _) = Capture(() => new GitOpsCommands().Sync(FixtureDir("valid"), "Server=x;Database=y;"));

        Assert.Equal(0, exit);
        Assert.Equal(1, importer.Calls);
        Assert.NotNull(importer.LastConfig);
        Assert.Single(importer.LastConfig!.Standards);
        // The success line reports the same kind set the validate summary does, integrations included.
        Assert.Contains("Synced:", output, StringComparison.Ordinal);
        Assert.Contains("1 collector(s)", output, StringComparison.Ordinal);
        Assert.Contains("1 integration(s)", output, StringComparison.Ordinal);
    }

    private static string WriteTempConfig(string content)
    {
        var dir = Directory.CreateTempSubdirectory("fb-gitops-sync-warn-");
        File.WriteAllText(Path.Join(dir.FullName, "config.yaml"), content);
        return dir.FullName;
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }

        return count;
    }

    // A valid config with a Scope whose subject names no asset. Core flags the subject as a dangling-subject
    // Warning (discriminable by DiagnosticCode.ScopeSubjectUnresolved); validation stays valid.
    private const string DanglingScopeSubjectConfig = """
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
        kind: Scope
        id: scope-a
        title: Scope A
        subject: ghost-x
        requirement: req-a
        disposition: In
        """;

    // A declared Vendor asset with no owner yields a non-scope Core Warning ("no owner").
    private const string OwnerlessVendorConfig = """
        apiVersion: freeboard.dev/v1alpha1
        kind: Asset
        id: vendor-a
        title: Vendor A
        type: Vendor
        source: declared
        """;

    // A declared Department whose parent names an id no document defines. A dangling edge is a separate
    // predicate from a missing one, so it needs its own case.
    private const string UnknownParentConfig = """
        apiVersion: freeboard.dev/v1alpha1
        kind: Asset
        id: dept-orphan
        title: Orphan Department
        type: Department
        source: declared
        parent: ghost-parent
        """;

    // On the sync path the DB-less Core scope-subject-dangling Warning is suppressed in favour of the
    // importer's DB-accurate result; an EMPTY importer result means no scope-subject warning is printed.
    [Fact]
    public void SyncEmptyImportResultSuppressesCoreScopeSubjectWarning()
    {
        var importer = new FakeImporter { Result = ImportResult.Empty };
        PersistenceFactory.CreateImporter = _ => importer;
        PersistenceFactory.CreateMigrationRunner = _ => new FakeMigrationRunner { Current = true };

        var dir = WriteTempConfig(DanglingScopeSubjectConfig);
        try
        {
            var (exit, _, err) = Capture(() => new GitOpsCommands().Sync(dir, "Server=x;Database=y;"));

            Assert.Equal(0, exit);
            Assert.Equal(1, importer.Calls);
            // Neither the Core message ("resolves to no asset.") nor the importer message
            // ("resolves to no live asset.") is present.
            Assert.DoesNotContain("resolves to no", err, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // One unresolved importer subject prints EXACTLY one DB-accurate warning line.
    [Fact]
    public void SyncPrintsExactlyOneImporterUnresolvedSubjectWarning()
    {
        var importer = new FakeImporter { Result = new ImportResult(["ghost-1"]) };
        PersistenceFactory.CreateImporter = _ => importer;
        PersistenceFactory.CreateMigrationRunner = _ => new FakeMigrationRunner { Current = true };

        var (exit, _, err) = Capture(() => new GitOpsCommands().Sync(FixtureDir("valid"), "Server=x;Database=y;"));

        Assert.Equal(0, exit);
        Assert.Equal(
            1, Occurrences(err, "warning: scope subject 'ghost-1' resolves to no live asset."));
    }

    // A non-scope Core Warning (an ownerless declared Vendor) still prints on the sync path.
    [Fact]
    public void SyncPrintsNonScopeCoreWarning()
    {
        var importer = new FakeImporter { Result = ImportResult.Empty };
        PersistenceFactory.CreateImporter = _ => importer;
        PersistenceFactory.CreateMigrationRunner = _ => new FakeMigrationRunner { Current = true };

        var dir = WriteTempConfig(OwnerlessVendorConfig);
        try
        {
            var (exit, _, err) = Capture(() => new GitOpsCommands().Sync(dir, "Server=x;Database=y;"));

            Assert.Equal(0, exit);
            Assert.Contains("warning:", err, StringComparison.Ordinal);
            Assert.Contains("vendor-a", err, StringComparison.Ordinal);
            Assert.Contains("no owner", err, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // A dangling parent warns on the sync path too, naming the asset and the id nothing defines, and does
    // not block the import.
    [Fact]
    public void SyncPrintsUnknownParentWarningAndStillImports()
    {
        var importer = new FakeImporter { Result = ImportResult.Empty };
        PersistenceFactory.CreateImporter = _ => importer;
        PersistenceFactory.CreateMigrationRunner = _ => new FakeMigrationRunner { Current = true };

        var dir = WriteTempConfig(UnknownParentConfig);
        try
        {
            var (exit, _, err) = Capture(() => new GitOpsCommands().Sync(dir, "Server=x;Database=y;"));

            Assert.Equal(0, exit);
            Assert.Contains("dept-orphan", err, StringComparison.Ordinal);
            Assert.Contains("unknown parent 'ghost-parent'", err, StringComparison.Ordinal);
            Assert.Equal(1, importer.Calls);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // The importer's configured unresolved subjects print on the sync path without double-printing the
    // suppressed Core scope-subject Warning: the same subject id appears exactly once (the importer line).
    [Fact]
    public void SyncPrintsImporterUnresolvedSubjectsWithoutDoublePrintingCoreWarning()
    {
        var importer = new FakeImporter { Result = new ImportResult(["ghost-x"]) };
        PersistenceFactory.CreateImporter = _ => importer;
        PersistenceFactory.CreateMigrationRunner = _ => new FakeMigrationRunner { Current = true };

        var dir = WriteTempConfig(DanglingScopeSubjectConfig);
        try
        {
            var (exit, _, err) = Capture(() => new GitOpsCommands().Sync(dir, "Server=x;Database=y;"));

            Assert.Equal(0, exit);
            Assert.Contains(
                "warning: scope subject 'ghost-x' resolves to no live asset.", err, StringComparison.Ordinal);
            // The subject id appears once (importer line only); the suppressed Core warning would repeat it.
            Assert.Equal(1, Occurrences(err, "ghost-x"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
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

    // A read-only integrity violation makes sync exit 3 before importing, without --migrate.
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

    // The integrity gate also blocks import when --migrate is passed (never apply over a corrupt schema).
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
