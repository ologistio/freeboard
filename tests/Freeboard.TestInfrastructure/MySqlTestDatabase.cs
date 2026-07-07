using Freeboard.Persistence;
using MySqlConnector;

namespace Freeboard.TestInfrastructure;

/// <summary>
/// Provisions a throwaway MySQL database for an integration test from the
/// <c>FREEBOARD_TEST_DB</c> connection string. Integration tests SKIP cleanly when the
/// env var is absent. Each instance creates a uniquely-named database and drops it on
/// dispose, so tests are isolated and leave no residue. Shared by every test project
/// that touches real MySQL.
/// </summary>
public sealed class MySqlTestDatabase : IAsyncDisposable
{
    public const string EnvVar = "FREEBOARD_TEST_DB";

    private readonly string adminConnectionString;
    private readonly string databaseName;

    public string ConnectionString { get; }

    public IDbConnectionFactory ConnectionFactory { get; }

    private MySqlTestDatabase(string baseConnectionString)
    {
        // Disable pooling for test connections. Each test provisions a uniquely-named
        // throwaway database, so each gets its own pool; connections pooled against a
        // database that has since been dropped linger until the idle timeout and
        // accumulate across the parallel integration run, exhausting the server's
        // max_connections ("Too many connections" at OpenAsync). Test connections are
        // short-lived, so skipping the pool costs little and keeps the count bounded.
        var adminBuilder = new MySqlConnectionStringBuilder(baseConnectionString);
        adminBuilder.Database = string.Empty;
        adminBuilder.Pooling = false;
        adminConnectionString = adminBuilder.ConnectionString;

        databaseName = $"fb_test_{Guid.NewGuid():N}";

        var dbBuilder = new MySqlConnectionStringBuilder(baseConnectionString) { Database = databaseName, Pooling = false };
        ConnectionString = dbBuilder.ConnectionString;
        ConnectionFactory = new MySqlConnectionFactory(new PersistenceOptions { ConnectionString = ConnectionString });
    }

    /// <summary>
    /// Returns a fresh test database, or null when no <c>FREEBOARD_TEST_DB</c> is
    /// configured. Callers should Skip.If(db is null, ...).
    /// </summary>
    public static async Task<MySqlTestDatabase?> TryCreateAsync()
    {
        var baseConn = Environment.GetEnvironmentVariable(EnvVar);
        if (string.IsNullOrWhiteSpace(baseConn))
        {
            return null;
        }

        var db = new MySqlTestDatabase(baseConn);
        await using var admin = new MySqlConnection(db.adminConnectionString);
        await admin.OpenAsync();
        await using var cmd = admin.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE `{db.databaseName}` CHARACTER SET utf8mb4;";
        await cmd.ExecuteNonQueryAsync();
        return db;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await using var admin = new MySqlConnection(adminConnectionString);
            await admin.OpenAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText = $"DROP DATABASE IF EXISTS `{databaseName}`;";
            await cmd.ExecuteNonQueryAsync();
        }
        catch (MySqlException)
        {
            // Best-effort cleanup; a dropped-already or unreachable DB is not a test failure.
        }
    }
}
