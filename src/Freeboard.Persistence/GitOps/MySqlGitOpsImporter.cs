using System.Data.Common;
using Dapper;
using Freeboard.Core.GitOps;

namespace Freeboard.Persistence.GitOps;

/// <summary>
/// Imports a validated <see cref="GitOpsConfig"/> into MySQL in one DML transaction.
/// FK-safe order: upsert standards (with metadata), requirements (reference standards), controls (with
/// their evaluation rule), organisations (parent-before-child), vendors, evidence-collectors (reference
/// controls and vendors); prune absent scopes then upsert the new scope set (which references
/// organisations and standards); replace the whole requirement-scope set and the whole vendor-scope set
/// (delete-all then insert); replace all control->requirement join rows; then hard-remove absent domain
/// rows (organisations child-before-parent; absent evidence-collectors before vendors and controls, so
/// their RESTRICT FKs stay safe; vendors; controls; requirements before standards, so a removed
/// standard's requirements clear before the RESTRICT FK is hit; standards). The requirement-scope and
/// vendor-scope replaces and the absent-collector prune precede the absent-organisation, absent-vendor,
/// absent-control, and absent-requirement deletes, keeping those RESTRICT FKs safe. Matches on id only.
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
        await UpsertControlsAsync(connection, transaction, plan.Controls, now, cancellationToken).ConfigureAwait(false);
        await UpsertOrganisationsAsync(connection, transaction, plan.Organisations, now, cancellationToken).ConfigureAwait(false);

        // Vendors are independent identity+metadata rows (like controls); upsert them here so their
        // referencing vendor_scopes can be replaced below. Absent vendors are pruned only after the
        // vendor_scopes replace, since the RESTRICT FK blocks deleting a still-referenced vendor.
        await UpsertAsync(
            connection, transaction, "vendors", plan.Vendors, now, cancellationToken).ConfigureAwait(false);

        // Evidence-collectors reference both controls and vendors, so upsert them after both. Upsert by
        // id (no secondary unique key); absent collectors are pruned before their target rows in step 6.
        await UpsertEvidenceCollectorsAsync(
            connection, transaction, plan.EvidenceCollectors, now, cancellationToken).ConfigureAwait(false);

        // 2. Prune absent scopes before upserting the new set. A scope whose id is renamed while
        //    keeping its (organisation, standard) pair collides on the unique key: the upsert would
        //    update the old-id row in place, then the absent-id cleanup below would delete that old
        //    id, dropping the pair entirely. Deleting first frees the pair so the new id inserts.
        await DeleteAbsentAsync(connection, transaction, "scopes", plan.ScopeIds, cancellationToken).ConfigureAwait(false);
        await UpsertScopesAsync(connection, transaction, plan.Scopes, now, cancellationToken).ConfigureAwait(false);

        // 3. Replace the whole requirement-scope set (delete-all then insert). A plain upsert is
        //    unsafe here: requirement_scopes has both a primary key (id) and a unique
        //    (organisation, requirement) key, so when rows swap pairs while keeping their ids,
        //    INSERT ... ON DUPLICATE KEY UPDATE can match the pair key and update the wrong row.
        //    Nothing references requirement_scopes, so a whole-set replace is safe and correct; it
        //    subsumes the absent-row prune. It re-inserts only rows referencing in-config
        //    organisations and requirements, so the absent-organisation and absent-requirement
        //    deletes below stay RESTRICT-safe.
        await ReplaceRequirementScopesAsync(connection, transaction, plan.RequirementScopes, now, cancellationToken).ConfigureAwait(false);

        // 3b. Replace the whole vendor-scope set (delete-all then insert), same reasoning as
        //     requirement_scopes: it has both a primary key (id) and unique (vendor, requirement) /
        //     (vendor, control) keys, so a pair-swap keeping ids cannot be upserted safely. It
        //     re-inserts only rows referencing in-config vendors, requirements, and controls, so the
        //     absent-vendor, absent-requirement, and absent-control deletes below stay RESTRICT-safe.
        await ReplaceVendorScopesAsync(connection, transaction, plan.VendorScopes, now, cancellationToken).ConfigureAwait(false);

        // 4. Replace all control->requirement join rows for the imported set (whole-set delete+insert).
        await ReplaceControlRequirementsAsync(connection, transaction, plan, cancellationToken).ConfigureAwait(false);

        // 5. Prune org-scoped role assignments for absent organisations before the org delete: the
        //    organisation FK is ON DELETE RESTRICT, so a stale assignment would wedge the delete. The
        //    importer needs no role semantics, only the prune, mirroring how it prunes absent scopes.
        await DeleteAbsentOrganisationAssignmentsAsync(connection, transaction, plan.OrganisationIds, cancellationToken).ConfigureAwait(false);

        // 6. Hard-remove remaining domain rows whose id is absent, FK-safe order (organisations
        //    child-before-parent; controls; requirements before standards; standards). OrganisationIds
        //    is parent-before-child, so reversing it deletes children first. Requirements reference
        //    standards with ON DELETE RESTRICT, so a removed standard's requirements must clear first.
        await DeleteAbsentOrganisationsAsync(connection, transaction, plan.OrganisationIds, cancellationToken).ConfigureAwait(false);
        // Prune absent evidence_collectors before their target rows: the collector FKs to controls and
        // vendors are RESTRICT, so a still-referenced control or vendor cannot be deleted while a stale
        // collector points at it.
        await DeleteAbsentAsync(connection, transaction, "evidence_collectors", plan.EvidenceCollectorIds, cancellationToken).ConfigureAwait(false);
        // Absent vendors are pruned after their vendor_scopes are gone (step 3b). Absent controls and
        // requirements are pruned after vendor_scopes too, so a vendor-scope's RESTRICT FK to a removed
        // control/requirement is already cleared.
        await DeleteAbsentAsync(connection, transaction, "vendors", plan.VendorIds, cancellationToken).ConfigureAwait(false);
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

    private static async Task UpsertControlsAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyList<ControlRowPlan> rows,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }

        // Controls carry the optional evaluation rule, so they cannot use the generic UpsertAsync.
        const string sql =
            "INSERT INTO controls (id, api_version, title, evaluation, created_at, updated_at) "
            + "VALUES (@Id, @ApiVersion, @Title, @Evaluation, @Now, @Now) "
            + "ON DUPLICATE KEY UPDATE "
            + "api_version = VALUES(api_version), title = VALUES(title), evaluation = VALUES(evaluation), "
            + "updated_at = VALUES(updated_at);";

        var parameters = rows.Select(r => new { r.Id, r.ApiVersion, r.Title, r.Evaluation, Now = now });
        await connection.ExecuteAsync(new CommandDefinition(sql, parameters, transaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    private static async Task UpsertEvidenceCollectorsAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyList<EvidenceCollectorRowPlan> rows,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }

        // Upsert by id (identity is id only, no secondary unique key). ConfigJson is written straight
        // into the native JSON column, which validates its well-formedness.
        const string sql =
            "INSERT INTO evidence_collectors "
            + "(id, api_version, title, control_id, vendor_id, type, frequency, threshold, config, created_at, updated_at) "
            + "VALUES (@Id, @ApiVersion, @Title, @Control, @Vendor, @Type, @Frequency, @Threshold, @ConfigJson, @Now, @Now) "
            + "ON DUPLICATE KEY UPDATE "
            + "api_version = VALUES(api_version), title = VALUES(title), control_id = VALUES(control_id), "
            + "vendor_id = VALUES(vendor_id), type = VALUES(type), frequency = VALUES(frequency), "
            + "threshold = VALUES(threshold), config = VALUES(config), updated_at = VALUES(updated_at);";

        var parameters = rows.Select(r => new
        {
            r.Id, r.ApiVersion, r.Title, r.Control, r.Vendor, r.Type, r.Frequency, r.Threshold, r.ConfigJson, Now = now,
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

    private static async Task ReplaceRequirementScopesAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyList<RequirementScopeRowPlan> rows,
        DateTime now,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM requirement_scopes;", transaction: transaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        if (rows.Count == 0)
        {
            return;
        }

        // Plain INSERT after the delete: config validation guarantees unique ids and unique
        // (organisation, requirement) pairs within the set, so no duplicate key can arise.
        const string sql =
            "INSERT INTO requirement_scopes (id, api_version, title, organisation_id, requirement_id, disposition, created_at, updated_at) "
            + "VALUES (@Id, @ApiVersion, @Title, @Organisation, @Requirement, @Disposition, @Now, @Now);";

        var parameters = rows.Select(r => new
        {
            r.Id, r.ApiVersion, r.Title, r.Organisation, r.Requirement, r.Disposition, Now = now,
        });
        await connection.ExecuteAsync(new CommandDefinition(sql, parameters, transaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    private static async Task ReplaceVendorScopesAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyList<VendorScopeRowPlan> rows,
        DateTime now,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM vendor_scopes;", transaction: transaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        if (rows.Count == 0)
        {
            return;
        }

        // Plain INSERT after the delete: config validation guarantees unique ids and unique
        // (vendor, requirement) / (vendor, control) pairs, so no duplicate key can arise. Exactly one
        // of requirement_id / control_id is non-null per row (the other side of the CHECK).
        const string sql =
            "INSERT INTO vendor_scopes "
            + "(id, api_version, title, vendor_id, requirement_id, control_id, disposition, justification, created_at, updated_at) "
            + "VALUES (@Id, @ApiVersion, @Title, @Vendor, @Requirement, @Control, @Disposition, @Justification, @Now, @Now);";

        var parameters = rows.Select(r => new
        {
            r.Id, r.ApiVersion, r.Title, r.Vendor, r.Requirement, r.Control, r.Disposition, r.Justification, Now = now,
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

    private static async Task DeleteAbsentOrganisationAssignmentsAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyList<string> keepIds,
        CancellationToken cancellationToken)
    {
        var sql = keepIds.Count == 0
            ? "DELETE FROM authz_organisation_role_assignments;"
            : "DELETE FROM authz_organisation_role_assignments WHERE organisation_id NOT IN @KeepIds;";

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
