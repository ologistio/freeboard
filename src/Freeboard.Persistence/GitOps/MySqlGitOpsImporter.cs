using System.Data.Common;
using Dapper;
using Freeboard.Core.GitOps;

namespace Freeboard.Persistence.GitOps;

/// <summary>
/// Imports a validated <see cref="GitOpsConfig"/> into MySQL in one DML transaction.
/// FK-safe order: upsert all domain rows by id, replace all cross-ref join rows for
/// the imported set, then hard-remove domain rows whose id is absent (scopes,
/// controls, standards). Matches on id only.
/// </summary>
public sealed class MySqlGitOpsImporter(IDbConnectionFactory connectionFactory) : IGitOpsImporter
{
    public async Task ImportAsync(GitOpsConfig config, CancellationToken cancellationToken = default)
    {
        var plan = ImportPlan.From(config);
        var now = DateTime.UtcNow;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // 1. Upsert domain rows by id (standards, controls, scopes).
        await UpsertAsync(connection, transaction, "standards", plan.Standards, now, cancellationToken).ConfigureAwait(false);
        await UpsertAsync(connection, transaction, "controls", plan.Controls, now, cancellationToken).ConfigureAwait(false);
        await UpsertAsync(connection, transaction, "scopes", plan.Scopes, now, cancellationToken).ConfigureAwait(false);

        // 2. Replace all cross-ref join rows for the imported set (whole-set delete+insert).
        await ReplaceControlStandardsAsync(connection, transaction, plan, cancellationToken).ConfigureAwait(false);
        await ReplaceScopeControlsAsync(connection, transaction, plan, cancellationToken).ConfigureAwait(false);

        // 3. Hard-remove domain rows whose id is absent, FK-safe order (scopes, controls, standards).
        await DeleteAbsentAsync(connection, transaction, "scopes", plan.ScopeIds, cancellationToken).ConfigureAwait(false);
        await DeleteAbsentAsync(connection, transaction, "controls", plan.ControlIds, cancellationToken).ConfigureAwait(false);
        await DeleteAbsentAsync(connection, transaction, "standards", plan.StandardIds, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        IReadOnlyList<DomainRow> rows,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }

        // created_at on insert only; title/api_version/updated_at advance on duplicate.
        var sql =
            $"INSERT INTO {table} (id, api_version, title, created_at, updated_at) "
            + "VALUES (@Id, @ApiVersion, @Title, @Now, @Now) "
            + "ON DUPLICATE KEY UPDATE "
            + "api_version = VALUES(api_version), title = VALUES(title), updated_at = VALUES(updated_at);";

        var parameters = rows.Select(r => new { r.Id, r.ApiVersion, r.Title, Now = now });
        await connection.ExecuteAsync(new CommandDefinition(sql, parameters, transaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    private static async Task ReplaceControlStandardsAsync(
        DbConnection connection,
        DbTransaction transaction,
        ImportPlan plan,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM control_standards;", transaction: transaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        if (plan.ControlStandards.Count == 0)
        {
            return;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO control_standards (control_id, standard_id) VALUES (@ControlId, @StandardId);",
            plan.ControlStandards, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static async Task ReplaceScopeControlsAsync(
        DbConnection connection,
        DbTransaction transaction,
        ImportPlan plan,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM scope_controls;", transaction: transaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        if (plan.ScopeControls.Count == 0)
        {
            return;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO scope_controls (scope_id, control_id) VALUES (@ScopeId, @ControlId);",
            plan.ScopeControls, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static async Task DeleteAbsentAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        IReadOnlyList<string> keepIds,
        CancellationToken cancellationToken)
    {
        var sql = keepIds.Count == 0
            ? $"DELETE FROM {table};"
            : $"DELETE FROM {table} WHERE id NOT IN @KeepIds;";

        await connection.ExecuteAsync(new CommandDefinition(
            sql, new { KeepIds = keepIds }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
