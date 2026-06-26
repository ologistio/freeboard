using ConsoleAppFramework;
using Freeboard.Core.GitOps;
using Freeboard.Persistence.System;

namespace Freeboard.CLI;

/// <summary>
/// The <c>gitops</c> command group. Loads and validates declarative compliance config
/// from a directory. <c>validate</c> and <c>apply --dry-run</c> make no network calls
/// and write no state. <c>sync</c> connects to the database and writes the persisted
/// compliance set.
/// </summary>
public sealed class GitOpsCommands
{
    /// <summary>Validate a GitOps config directory.</summary>
    /// <param name="path">Directory containing the YAML config.</param>
    public int Validate([Argument] string path)
    {
        var result = ConfigValidator.LoadAndValidate(path);
        if (!result.IsValid)
        {
            PrintDiagnostics(result);
            return 1;
        }

        PrintSummary(result.Config);
        return 0;
    }

    /// <summary>Show the config state that would be applied (dry-run only).</summary>
    /// <param name="path">Directory containing the YAML config.</param>
    /// <param name="dryRun">Required in this version. Real apply lands in a later increment.</param>
    public int Apply([Argument] string path, bool dryRun = false)
    {
        if (!dryRun)
        {
            Console.Error.WriteLine(
                "Only 'apply --dry-run' is supported in this version. Real apply lands in a later increment.");
            return 2;
        }

        var result = ConfigValidator.LoadAndValidate(path);
        if (!result.IsValid)
        {
            PrintDiagnostics(result);
            return 1;
        }

        Console.WriteLine("Planned config state (dry-run, nothing written):");
        PrintPlannedState(result.Config);
        return 0;
    }

    /// <summary>Load, validate, and import a GitOps config into the persisted store.</summary>
    /// <param name="path">Directory containing the YAML config.</param>
    /// <param name="connectionString">-c, MySQL connection string. Overrides FREEBOARD_DB.</param>
    /// <param name="migrate">Apply pending schema migrations before importing.</param>
    public int Sync([Argument] string path, string? connectionString = null, bool migrate = false)
    {
        var result = ConfigValidator.LoadAndValidate(path);
        if (!result.IsValid)
        {
            PrintDiagnostics(result);
            return 1;
        }

        var resolved = ConnectionStringResolver.Resolve(connectionString);
        if (resolved is null)
        {
            Console.Error.WriteLine(
                $"No connection string. Pass --connection-string or set {ConnectionStringResolver.EnvVar}.");
            return 3;
        }

        try
        {
            var runner = PersistenceFactory.CreateMigrationRunner(resolved);

            // Migrate-first gate: GetState is read-only and creates nothing.
            var state = runner.GetStateAsync().GetAwaiter().GetResult();

            // An integrity-violated schema must never be imported into, with or without
            // --migrate. GetState reports this by a pure read; fail before importing.
            if (state.IsCorrupt)
            {
                Console.Error.WriteLine($"{state.IntegrityError} Nothing was written.");
                return 3;
            }

            if (!state.IsCurrent)
            {
                if (!migrate)
                {
                    Console.Error.WriteLine(
                        "Schema out of date; run 'system migrate' or pass --migrate. Nothing was written.");
                    return 3;
                }

                runner.ApplyPendingAsync().GetAwaiter().GetResult();
            }

            var importer = PersistenceFactory.CreateImporter(resolved);
            importer.ImportAsync(result.Config).GetAwaiter().GetResult();
        }
        catch (MigrationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 3;
        }
        catch (Exception ex) when (ex is global::System.Data.Common.DbException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Database operation failed: {ex.Message}");
            return 3;
        }

        Console.WriteLine(
            $"Synced: {result.Config.Standards.Count} standard(s), {result.Config.Controls.Count} control(s), "
            + $"{result.Config.Scopes.Count} scope(s).");
        return 0;
    }

    private static void PrintDiagnostics(ConfigResult result)
    {
        foreach (var diagnostic in result.Diagnostics)
        {
            Console.Error.WriteLine(diagnostic.ToString());
        }
    }

    private static void PrintSummary(GitOpsConfig config)
    {
        Console.WriteLine(
            $"OK: {config.Standards.Count} standard(s), {config.Controls.Count} control(s), {config.Scopes.Count} scope(s).");
    }

    private static void PrintPlannedState(GitOpsConfig config)
    {
        Console.WriteLine($"Standards ({config.Standards.Count}):");
        foreach (var standard in config.Standards)
        {
            Console.WriteLine($"  - {standard.Id}: {standard.Title}");
        }

        Console.WriteLine($"Controls ({config.Controls.Count}):");
        foreach (var control in config.Controls)
        {
            Console.WriteLine($"  - {control.Id}: {control.Title} -> [{string.Join(", ", control.MapsTo)}]");
        }

        Console.WriteLine($"Scopes ({config.Scopes.Count}):");
        foreach (var scope in config.Scopes)
        {
            Console.WriteLine($"  - {scope.Id}: {scope.Title} -> [{string.Join(", ", scope.Controls)}]");
        }
    }
}
