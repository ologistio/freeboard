using System.Data.Common;
using System.Reflection;
using Dapper;
using MySqlConnector;

namespace Freeboard.Persistence.System;

/// <summary>
/// Applies the embedded hand-written SQL migrations against MySQL and reports
/// current/pending state. Bootstraps <c>schema_migrations</c> only in
/// <see cref="ApplyPendingAsync"/>. <see cref="GetStateAsync"/> is strictly read-only.
/// </summary>
public sealed class MySqlMigrationRunner : IMigrationRunner
{
    private readonly IDbConnectionFactory connectionFactory;
    private readonly Assembly migrationAssembly;

    public MySqlMigrationRunner(IDbConnectionFactory connectionFactory)
        : this(connectionFactory, typeof(MySqlMigrationRunner).Assembly)
    {
    }

    /// <summary>
    /// Overload for tests that need a controllable migration set (a different assembly
    /// whose embedded <c>Migrations/*.sql</c> drive the runner).
    /// </summary>
    public MySqlMigrationRunner(IDbConnectionFactory connectionFactory, Assembly migrationAssembly)
    {
        this.connectionFactory = connectionFactory;
        this.migrationAssembly = migrationAssembly;
    }

    private const string BootstrapSql =
        """
        CREATE TABLE IF NOT EXISTS schema_migrations (
            version VARCHAR(190) NOT NULL,
            checksum CHAR(64) NOT NULL,
            applied_at DATETIME(6) NOT NULL,
            PRIMARY KEY (version)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        """;

    public async Task<MigrationState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var embedded = MigrationCatalog.Load(migrationAssembly);

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        if (!await TableExistsAsync(connection, "schema_migrations", cancellationToken).ConfigureAwait(false))
        {
            // Fresh DB: every embedded migration is pending. No table is created.
            return new MigrationState([], embedded.Select(m => m.Version).ToList());
        }

        var applied = await ReadAppliedAsync(connection, cancellationToken).ConfigureAwait(false);
        return MigrationPlanner.Classify(embedded, applied);
    }

    public async Task<IReadOnlyList<string>> ApplyPendingAsync(CancellationToken cancellationToken = default)
    {
        var embedded = MigrationCatalog.Load(migrationAssembly);

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Bootstrap the tracking table first so applied versions can be recorded.
        await connection.ExecuteAsync(new CommandDefinition(BootstrapSql, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        var applied = await ReadAppliedAsync(connection, cancellationToken).ConfigureAwait(false);

        // Forward-only integrity: fail loudly on deleted/renamed or edited applied
        // migrations before running anything.
        MigrationPlanner.Validate(embedded, applied);

        var appliedVersions = new HashSet<string>(applied.Select(a => a.Version), StringComparer.Ordinal);
        var newlyApplied = new List<string>();

        foreach (var migration in embedded)
        {
            if (appliedVersions.Contains(migration.Version))
            {
                continue;
            }

            await ApplyOneAsync(connection, migration, cancellationToken).ConfigureAwait(false);
            newlyApplied.Add(migration.Version);
        }

        return newlyApplied;
    }

    private static async Task ApplyOneAsync(
        DbConnection connection,
        EmbeddedMigration migration,
        CancellationToken cancellationToken)
    {
        try
        {
            // DDL implicit-commits on MySQL, so this is not atomic. We record the version
            // only after the statements succeed; on partial failure the version stays
            // unrecorded and the migration is re-attemptable.
            await connection.ExecuteAsync(new CommandDefinition(migration.Sql, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.BinLogCreateRoutineNeedSuper)
        {
            // Creating a trigger under binary logging needs either a server with
            // log_bin_trust_function_creators=1 or a migration user privileged to create routines.
            // Name the remediation so the operator does not have to decode error 1419.
            throw new MigrationException(
                $"Migration '{migration.Version}' failed: creating a trigger requires the server to run "
                + "with log_bin_trust_function_creators=1, or the migration database user to hold a "
                + "privilege sufficient to create triggers under binary logging. "
                + "The version was not recorded; fix the grant or server setting and re-run.",
                ex);
        }
        catch (DbException ex)
        {
            throw new MigrationException(
                $"Migration '{migration.Version}' failed during execution: {ex.Message}. "
                + "The version was not recorded; re-run to re-attempt.");
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO schema_migrations (version, checksum, applied_at) VALUES (@Version, @Checksum, @AppliedAt);",
            new { migration.Version, migration.Checksum, AppliedAt = DateTime.UtcNow },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static async Task<bool> TableExistsAsync(
        DbConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        var count = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM information_schema.tables "
            + "WHERE table_schema = DATABASE() AND table_name = @table;",
            new { table },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return count > 0;
    }

    private static async Task<IReadOnlyList<AppliedMigration>> ReadAppliedAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        var rows = await connection.QueryAsync<AppliedMigration>(new CommandDefinition(
            "SELECT version AS Version, checksum AS Checksum FROM schema_migrations;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToList();
    }
}
