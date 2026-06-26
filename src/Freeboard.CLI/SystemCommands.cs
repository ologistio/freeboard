using ConsoleAppFramework;
using Freeboard.Persistence.System;

namespace Freeboard.CLI;

/// <summary>
/// The <c>system</c> command group. Platform/schema operations. Connects to the
/// database; the schema is a system concern owned independently of any one writer.
/// </summary>
public sealed class SystemCommands
{
    /// <summary>Apply pending schema migrations.</summary>
    /// <param name="connectionString">-c, MySQL connection string. Overrides FREEBOARD_DB.</param>
    public int Migrate(string? connectionString = null)
    {
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
            var applied = runner.ApplyPendingAsync().GetAwaiter().GetResult();

            Console.WriteLine(applied.Count == 0
                ? "Schema is up to date; no migrations applied."
                : $"Applied {applied.Count} migration(s): {string.Join(", ", applied)}.");
            return 0;
        }
        catch (MigrationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 3;
        }
        catch (Exception ex) when (ex is global::System.Data.Common.DbException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Migration failed: {ex.Message}");
            return 3;
        }
    }
}
