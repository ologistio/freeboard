using Freeboard.Persistence;
using Freeboard.Persistence.GitOps;
using Freeboard.Persistence.System;

namespace Freeboard.CLI;

/// <summary>
/// Constructs the persistence services for the CLI from a connection string. A static
/// seam so tests can substitute importer/migration-runner doubles without a live
/// database. Defaults build the real MySQL-backed implementations.
/// </summary>
internal static class PersistenceFactory
{
    public static Func<string, IGitOpsImporter> CreateImporter { get; set; } =
        connectionString => new MySqlGitOpsImporter(Factory(connectionString));

    public static Func<string, IMigrationRunner> CreateMigrationRunner { get; set; } =
        connectionString => new MySqlMigrationRunner(Factory(connectionString));

    private static IDbConnectionFactory Factory(string connectionString) =>
        new MySqlConnectionFactory(new PersistenceOptions { ConnectionString = connectionString });
}
