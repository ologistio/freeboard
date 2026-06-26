using System.Data.Common;
using MySqlConnector;

namespace Freeboard.Persistence;

/// <summary>
/// Opens a MySQL connection. A seam over MySqlConnector so the store, importer, and
/// migration runner depend on an abstraction rather than the concrete client.
/// </summary>
public interface IDbConnectionFactory
{
    Task<DbConnection> OpenAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Default factory: opens a <see cref="MySqlConnection"/> from the configured
/// connection string.
/// </summary>
public sealed class MySqlConnectionFactory(PersistenceOptions options) : IDbConnectionFactory
{
    private readonly string connectionString = options.ConnectionString;

    public async Task<DbConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
