using System.Data.Common;
using Dapper;
using Freeboard.Core.GitOps;

namespace Freeboard.Persistence.GitOps;

/// <summary>
/// Imports a validated <see cref="GitOpsConfig"/> into MySQL in one DML transaction.
/// FK-safe order: upsert standards (with metadata), requirements (reference standards), controls,
/// organisations (parent-before-child); prune absent scopes then upsert the new scope set (which
/// references organisations and standards); prune absent requirement-scopes then upsert the new set
/// (which references organisations and requirements); replace all control->requirement join rows;
/// then hard-remove absent domain rows (organisations child-before-parent; controls; requirements
/// before standards, so a removed standard's requirements clear before the RESTRICT FK is hit;
/// standards). The requirement-scope prune precedes the absent-organisation and absent-requirement
/// deletes, keeping those RESTRICT FKs safe. Matches on id only.
/// </summary>
public sealed class MySqlGitOpsImporter(IDbConnectionFactory connectionFactory) : IGitOpsImporter
{
    public async Task ImportAsync(GitOpsConfig config, CancellationToken cancellationToken = default)
    {
        var plan = ImportPlan.From(config);
        var now = DateTime.UtcNow;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // 1. Upsert domain rows by id, FK-safe (standards, requirements, controls, organisations).
        //    Requirements reference standards, so they follow standards and precede any standard delete.
        await UpsertStandardsAsync(connection, transaction, plan.Standards, now, cancellationToken).ConfigureAwait(false);
        await UpsertRequirementsAsync(connection, transaction, plan.Requirements, now, cancellationToken).ConfigureAwait(false);
        await UpsertAsync(connection, transaction, "controls", plan.Controls, now, cancellationToken).ConfigureAwait(false);
        await UpsertOrganisationsAsync(connection, transaction, plan.Organisations, now, cancellationToken).ConfigureAwait(false);

        // 2. Prune absent scopes before upserting the new set. A scope whose id is renamed while
        //    keeping its (organisation, standard) pair collides on the unique key: the upsert would
        //    update the old-id row in place, then the absent-id cleanup below would delete that old
        //    id, dropping the pair entirely. Deleting first frees the pair so the new id inserts.
        await DeleteAbsentAsync(connection, transaction, "scopes", plan.ScopeIds, cancellationToken).ConfigureAwait(false);
        await UpsertScopesAsync(connection, transaction, plan.Scopes, now, cancellationToken).ConfigureAwait(false);

        // 3. Prune absent requirement-scopes before upserting the new set (same rename-safety reason
        //    as scopes above, keyed on (organisation, requirement)). Requirement-scopes reference
        //    organisations and requirements, both already upserted. Pruning here, before the absent-
        //    organisation and absent-requirement deletes below, keeps those RESTRICT FKs safe.
        await DeleteAbsentAsync(connection, transaction, "requirement_scopes", plan.RequirementScopeIds, cancellationToken).ConfigureAwait(false);
        await UpsertRequirementScopesAsync(connection, transaction, plan.RequirementScopes, now, cancellationToken).ConfigureAwait(false);

        // 4. Replace all control->requirement join rows for the imported set (whole-set delete+insert).
        await ReplaceControlRequirementsAsync(connection, transaction, plan, cancellationToken).ConfigureAwait(false);

        // 5. Hard-remove remaining domain rows whose id is absent, FK-safe order (organisations
        //    child-before-parent; controls; requirements before standards; standards). OrganisationIds
        //    is parent-before-child, so reversing it deletes children first. Requirements reference
        //    standards with ON DELETE RESTRICT, so a removed standard's requirements must clear first.
        await DeleteAbsentOrganisationsAsync(connection, transaction, plan.OrganisationIds, cancellationToken).ConfigureAwait(false);
        await DeleteAbsentAsync(connection, transaction, "controls", plan.ControlIds, cancellationToken).ConfigureAwait(false);
        await DeleteAbsentAsync(connection, transaction, "requirements", plan.RequirementIds, cancellationToken).ConfigureAwait(false);
        await DeleteAbsentAsync(connection, transaction, "standards", plan.StandardIds, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertStandardsAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyList<StandardRowPlan> rows,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }

        const string sql =
            "INSERT INTO standards (id, api_version, title, version, authority, publisher, source_url, created_at, updated_at) "
            + "VALUES (@Id, @ApiVersion, @Title, @Version, @Authority, @Publisher, @SourceUrl, @Now, @Now) "
            + "ON DUPLICATE KEY UPDATE "
            + "api_version = VALUES(api_version), title = VALUES(title), version = VALUES(version), "
            + "authority = VALUES(authority), publisher = VALUES(publisher), source_url = VALUES(source_url), "
            + "updated_at = VALUES(updated_at);";

        var parameters = rows.Select(r => new
        {
            r.Id, r.ApiVersion, r.Title, r.Version, r.Authority, r.Publisher, r.SourceUrl, Now = now,
        });
        await connection.ExecuteAsync(new CommandDefinition(sql, parameters, transaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    private static async Task UpsertRequirementsAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyList<RequirementRowPlan> rows,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }

        const string sql =
            "INSERT INTO requirements "
            + "(id, api_version, title, standard_id, theme, statement, guidance, citation_label, citation_url, created_at, updated_at) "
            + "VALUES (@Id, @ApiVersion, @Title, @Standard, @Theme, @Statement, @Guidance, @CitationLabel, @CitationUrl, @Now, @Now) "
            + "ON DUPLICATE KEY UPDATE "
            + "api_version = VALUES(api_version), title = VALUES(title), standard_id = VALUES(standard_id), "
            + "theme = VALUES(theme), statement = VALUES(statement), guidance = VALUES(guidance), "
            + "citation_label = VALUES(citation_label), citation_url = VALUES(citation_url), updated_at = VALUES(updated_at);";

        var parameters = rows.Select(r => new
        {
            r.Id, r.ApiVersion, r.Title, r.Standard, r.Theme, r.Statement, r.Guidance, r.CitationLabel, r.CitationUrl, Now = now,
        });
        await connection.ExecuteAsync(new CommandDefinition(sql, parameters, transaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
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

    private static async Task UpsertOrganisationsAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyList<OrganisationRowPlan> rows,
        DateTime now,
        CancellationToken cancellationToken)
    {
        // Insert one row at a time in parent-before-child order so the self-FK holds mid-transaction.
        const string sql =
            "INSERT INTO organisations (id, api_version, title, kind, parent_id, created_at, updated_at) "
            + "VALUES (@Id, @ApiVersion, @Title, @Kind, @Parent, @Now, @Now) "
            + "ON DUPLICATE KEY UPDATE "
            + "api_version = VALUES(api_version), title = VALUES(title), kind = VALUES(kind), "
            + "parent_id = VALUES(parent_id), updated_at = VALUES(updated_at);";

        foreach (var row in rows)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                sql,
                new { row.Id, row.ApiVersion, row.Title, row.Kind, row.Parent, Now = now },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }

    private static async Task UpsertScopesAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyList<ScopeRowPlan> rows,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }

        const string sql =
            "INSERT INTO scopes (id, api_version, title, organisation_id, standard_id, disposition, created_at, updated_at) "
            + "VALUES (@Id, @ApiVersion, @Title, @Organisation, @Standard, @Disposition, @Now, @Now) "
            + "ON DUPLICATE KEY UPDATE "
            + "api_version = VALUES(api_version), title = VALUES(title), organisation_id = VALUES(organisation_id), "
            + "standard_id = VALUES(standard_id), disposition = VALUES(disposition), updated_at = VALUES(updated_at);";

        var parameters = rows.Select(r => new
        {
            r.Id, r.ApiVersion, r.Title, r.Organisation, r.Standard, r.Disposition, Now = now,
        });
        await connection.ExecuteAsync(new CommandDefinition(sql, parameters, transaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    private static async Task UpsertRequirementScopesAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyList<RequirementScopeRowPlan> rows,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }

        const string sql =
            "INSERT INTO requirement_scopes (id, api_version, title, organisation_id, requirement_id, disposition, created_at, updated_at) "
            + "VALUES (@Id, @ApiVersion, @Title, @Organisation, @Requirement, @Disposition, @Now, @Now) "
            + "ON DUPLICATE KEY UPDATE "
            + "api_version = VALUES(api_version), title = VALUES(title), organisation_id = VALUES(organisation_id), "
            + "requirement_id = VALUES(requirement_id), disposition = VALUES(disposition), updated_at = VALUES(updated_at);";

        var parameters = rows.Select(r => new
        {
            r.Id, r.ApiVersion, r.Title, r.Organisation, r.Requirement, r.Disposition, Now = now,
        });
        await connection.ExecuteAsync(new CommandDefinition(sql, parameters, transaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    private static async Task ReplaceControlRequirementsAsync(
        DbConnection connection,
        DbTransaction transaction,
        ImportPlan plan,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM control_requirements;", transaction: transaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        if (plan.ControlRequirements.Count == 0)
        {
            return;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO control_requirements (control_id, requirement_id) VALUES (@ControlId, @RequirementId);",
            plan.ControlRequirements, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
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

    private static async Task DeleteAbsentOrganisationsAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyList<string> keepIds,
        CancellationToken cancellationToken)
    {
        // The self-FK is ON DELETE RESTRICT, so an absent parent cannot be deleted while an
        // absent child still points at it. Each pass deletes only absent leaves (rows that are no
        // longer any row's parent), repeating until no absent rows remain. This removes children
        // before parents without needing the depth order here.
        var keepClause = keepIds.Count == 0 ? string.Empty : "id NOT IN @KeepIds AND ";
        var sql =
            $"DELETE FROM organisations WHERE {keepClause}"
            + "id NOT IN (SELECT parent_id FROM (SELECT parent_id FROM organisations WHERE parent_id IS NOT NULL) AS p);";

        while (true)
        {
            var deleted = await connection.ExecuteAsync(new CommandDefinition(
                sql, new { KeepIds = keepIds }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (deleted == 0)
            {
                break;
            }
        }
    }
}
