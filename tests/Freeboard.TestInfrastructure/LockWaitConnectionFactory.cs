using System.Data.Common;
using Freeboard.Persistence;

namespace Freeboard.TestInfrastructure;

/// <summary>
/// Decorates an <see cref="IDbConnectionFactory"/> so every connection it opens carries a low
/// <c>innodb_lock_wait_timeout</c>. A store given this factory fails fast, with the SERVER's own
/// <c>ER_LOCK_WAIT_TIMEOUT</c>, when it blocks on a lock another transaction holds - which is the error
/// shape a store's lock-failure mapping has to recognize, and is what a client-side command timeout
/// would not produce.
///
/// The setting is per SESSION on purpose. The server global is shared by the parallel integration run,
/// so setting it there would change every other test's lock-wait behaviour. Issuing it on open is safe
/// because <see cref="MySqlTestDatabase"/> builds its connection strings with pooling off: every open is
/// a fresh session and the setting dies with the connection.
/// </summary>
public sealed class LockWaitConnectionFactory(IDbConnectionFactory inner, int seconds) : IDbConnectionFactory
{
    public async Task<DbConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = await inner.OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SET SESSION innodb_lock_wait_timeout = {seconds};";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Pooling is off, so an undisposed connection holds its server session until finalization.
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return connection;
    }
}
