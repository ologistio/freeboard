using System.Data.Common;
using Dapper;
using Freeboard.Core.GitOps;

namespace Freeboard.Persistence.GitOps;

/// <summary>
/// Imports a validated <see cref="GitOpsConfig"/> into MySQL in one DML transaction.
/// First fails the whole sync if a declared id collides with an existing discovered asset (so a discovered
/// row is never rewritten). FK-safe order: upsert standards (with metadata), requirements (reference
/// standards), controls (with their evaluation rule), declared assets (Company/Department/Vendor, one id
/// space, no parent-before-child order since assets.parent has no FK), integration-connections (reference
/// vendor assets), evidence-collectors (reference controls, vendor assets, and integration-connections),
/// attestation-templates (reference controls); prune absent scopes then upsert the new scope set (which
/// references assets and standards); replace the whole requirement-scope set and the whole vendor-scope set
/// (delete-all then insert); replace all control->requirement join rows; then hard-remove absent rows:
/// org role assignments, absent evidence-collectors and attestation-templates, absent
/// integration-connections, then ONE source = 'declared'-guarded declared-asset prune (which never touches
/// a discovered row) after every asset-referencing row is gone, then controls, requirements before
/// standards. Matches on id only.
/// </summary>
public sealed class MySqlGitOpsImporter(IDbConnectionFactory connectionFactory) : IGitOpsImporter
{
    public async Task ImportAsync(GitOpsConfig config, CancellationToken cancellationToken = default)
    {
        var plan = ImportPlan.From(config);
        var now = DateTime.UtcNow;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Before any write: a declared id that collides with an existing discovered ULID would rewrite
        // that discovered row on upsert (flipping its source, blanking its discovered fields), violating
        // "sync never touches a discovered asset". Fail the whole sync here, inside the transaction, so it
        // rolls back and mutates nothing. The CLI maps this to exit 3 (operational), distinct from an
        // exit-1 config-validation error found before any DB connection.
        await GuardDeclaredDiscoveredCollisionAsync(connection, transaction, plan.AssetIds, cancellationToken).ConfigureAwait(false);

        // 1. Upsert domain rows by id, FK-safe (standards, requirements, controls, assets).
        //    Requirements reference standards, so they follow standards and precede any standard delete.
        await UpsertStandardsAsync(connection, transaction, plan.Standards, now, cancellationToken).ConfigureAwait(false);
        await UpsertRequirementsAsync(connection, transaction, plan.Requirements, now, cancellationToken).ConfigureAwait(false);
        await UpsertControlsAsync(connection, transaction, plan.Controls, now, cancellationToken).ConfigureAwait(false);

        // Declared assets (Company/Department/Vendor) are one id space, upserted together. assets.parent
        // has no FK, so no parent-before-child order is needed. Their referencing scopes, vendor_scopes,
        // and collectors are replaced/pruned below; absent declared assets are pruned after those, in one
        // source-guarded prune that never touches a discovered row.
        await UpsertAssetsAsync(connection, transaction, plan.Assets, now, cancellationToken).ConfigureAwait(false);

        // Integration-connections reference vendors, so upsert them after vendors and before the
        // evidence-collectors that reference them. Absent connections are pruned after absent collectors
        // and before absent vendors in step 6, keeping both RESTRICT FKs safe.
        await UpsertIntegrationConnectionsAsync(
            connection, transaction, plan.IntegrationConnections, now, cancellationToken).ConfigureAwait(false);

        // Evidence-collectors reference controls, vendors, and integration-connections, so upsert them
        // after all three. Upsert by id (no secondary unique key); absent collectors are pruned before
        // their target rows in step 6.
        await UpsertEvidenceCollectorsAsync(
            connection, transaction, plan.EvidenceCollectors, now, cancellationToken).ConfigureAwait(false);

        // Attestation-templates reference controls, so upsert them after controls. Upsert by id (no
        // secondary unique key); absent templates are pruned before their target controls in step 6.
        await UpsertAttestationTemplatesAsync(
            connection, transaction, plan.AttestationTemplates, now, cancellationToken).ConfigureAwait(false);

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

        // 6. Hard-remove remaining rows whose id is absent, FK-safe order. Prune absent evidence_collectors
        //    before their target rows: the collector FKs to controls and vendor assets are RESTRICT, so a
        //    still-referenced control or vendor asset cannot be deleted while a stale collector points at it.
        await DeleteAbsentAsync(connection, transaction, "evidence_collectors", plan.EvidenceCollectorIds, cancellationToken).ConfigureAwait(false);
        // Prune absent attestation_templates before their target controls: the control_id FK is RESTRICT,
        // so a still-referenced control cannot be deleted while a stale template points at it.
        await DeleteAbsentAsync(connection, transaction, "attestation_templates", plan.AttestationTemplateIds, cancellationToken).ConfigureAwait(false);
        // Prune absent integration_connections after absent evidence_collectors (whose connection_id FK
        // is RESTRICT) and before the declared-asset prune (the connection's vendor_id FK to a vendor
        // asset is RESTRICT), so both FKs stay satisfied.
        await DeleteAbsentAsync(connection, transaction, "integration_connections", plan.IntegrationConnectionIds, cancellationToken).ConfigureAwait(false);
        // The single declared-asset prune, guarded by source = 'declared' so it NEVER touches a discovered
        // row. It runs after every row that references an asset (scopes, requirement_scopes, vendor_scopes,
        // evidence_collectors, integration_connections, and the org role assignments) has been pruned or
        // replaced to only reference in-config assets, so the RESTRICT FKs into assets stay satisfied.
        // assets.parent has no FK, so removing a parent while a child survives is a tolerated dangling
        // edge, not an FK violation - no child-before-parent order is needed.
        await DeleteAbsentDeclaredAssetsAsync(connection, transaction, plan.AssetIds, cancellationToken).ConfigureAwait(false);
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
            r.Id,
            r.ApiVersion,
            r.Title,
            r.Version,
            r.Authority,
            r.Publisher,
            r.SourceUrl,
            Now = now,
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
            r.Id,
            r.ApiVersion,
            r.Title,
            r.Standard,
            r.Theme,
            r.Statement,
            r.Guidance,
            r.CitationLabel,
            r.CitationUrl,
            Now = now,
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

        // Upsert by id (identity is id only, no secondary unique key). ConfigJson and ChecksJson are
        // written straight into their native JSON columns, which validate well-formedness.
        const string sql =
            "INSERT INTO evidence_collectors "
            + "(id, api_version, title, control_id, vendor_id, connection_id, type, frequency, threshold, config, checks, created_at, updated_at) "
            + "VALUES (@Id, @ApiVersion, @Title, @Control, @Vendor, @Connection, @Type, @Frequency, @Threshold, @ConfigJson, @ChecksJson, @Now, @Now) "
            + "ON DUPLICATE KEY UPDATE "
            + "api_version = VALUES(api_version), title = VALUES(title), control_id = VALUES(control_id), "
            + "vendor_id = VALUES(vendor_id), connection_id = VALUES(connection_id), type = VALUES(type), "
            + "frequency = VALUES(frequency), threshold = VALUES(threshold), config = VALUES(config), "
            + "checks = VALUES(checks), updated_at = VALUES(updated_at);";

        var parameters = rows.Select(r => new
        {
            r.Id,
            r.ApiVersion,
            r.Title,
            r.Control,
            r.Vendor,
            r.Connection,
            r.Type,
            r.Frequency,
            r.Threshold,
            r.ConfigJson,
            r.ChecksJson,
            Now = now,
        });
        await connection.ExecuteAsync(new CommandDefinition(sql, parameters, transaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    private static async Task UpsertIntegrationConnectionsAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyList<IntegrationConnectionRowPlan> rows,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }

        // Upsert by id (identity is id only; a provider is not unique - one provider backs many
        // connections). The API token is never written here.
        const string sql =
            "INSERT INTO integration_connections "
            + "(id, api_version, title, provider, discovery_cadence, base_url, vendor_id, created_at, updated_at) "
            + "VALUES (@Id, @ApiVersion, @Title, @Provider, @DiscoveryCadence, @BaseUrl, @Vendor, @Now, @Now) "
            + "ON DUPLICATE KEY UPDATE "
            + "api_version = VALUES(api_version), title = VALUES(title), provider = VALUES(provider), "
            + "discovery_cadence = VALUES(discovery_cadence), base_url = VALUES(base_url), "
            + "vendor_id = VALUES(vendor_id), updated_at = VALUES(updated_at);";

        var parameters = rows.Select(r => new
        {
            r.Id,
            r.ApiVersion,
            r.Title,
            r.Provider,
            r.DiscoveryCadence,
            r.BaseUrl,
            r.Vendor,
            Now = now,
        });
        await connection.ExecuteAsync(new CommandDefinition(sql, parameters, transaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    private static async Task UpsertAttestationTemplatesAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyList<AttestationTemplateRowPlan> rows,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }

        // Upsert by id (identity is id only, no secondary unique key). FieldsJson/QuizJson are written
        // straight into the native JSON columns, which validate their well-formedness.
        const string sql =
            "INSERT INTO attestation_templates "
            + "(id, api_version, title, control_id, type, body, fields, pass_mark, quiz, created_at, updated_at) "
            + "VALUES (@Id, @ApiVersion, @Title, @Control, @Type, @Body, @FieldsJson, @PassMark, @QuizJson, @Now, @Now) "
            + "ON DUPLICATE KEY UPDATE "
            + "api_version = VALUES(api_version), title = VALUES(title), control_id = VALUES(control_id), "
            + "type = VALUES(type), body = VALUES(body), fields = VALUES(fields), pass_mark = VALUES(pass_mark), "
            + "quiz = VALUES(quiz), updated_at = VALUES(updated_at);";

        var parameters = rows.Select(r => new
        {
            r.Id,
            r.ApiVersion,
            r.Title,
            r.Control,
            r.Type,
            r.Body,
            r.FieldsJson,
            r.PassMark,
            r.QuizJson,
            Now = now,
        });
        await connection.ExecuteAsync(new CommandDefinition(sql, parameters, transaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    private static async Task GuardDeclaredDiscoveredCollisionAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyList<string> declaredIds,
        CancellationToken cancellationToken)
    {
        if (declaredIds.Count == 0)
        {
            return;
        }

        var collision = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT id FROM assets WHERE source = 'discovered' AND id IN @Ids LIMIT 1;",
            new { Ids = declaredIds }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (collision is not null)
        {
            throw new InvalidOperationException(
                $"Declared asset id '{collision}' collides with an existing discovered asset. Nothing was written.");
        }
    }

    private static async Task UpsertAssetsAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyList<AssetRowPlan> rows,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }

        // Declared rows always write source = 'declared' and leave the discovered-only columns null. No
        // parent-before-child order (assets.parent has no FK), so a single batched upsert is safe. The
        // collision guard above already ensured no id here matches a discovered row.
        const string sql =
            "INSERT INTO assets (id, type, source, api_version, title, parent, owner, created_at, updated_at) "
            + "VALUES (@Id, @Type, 'declared', @ApiVersion, @Title, @Parent, @Owner, @Now, @Now) "
            + "ON DUPLICATE KEY UPDATE "
            + "type = VALUES(type), api_version = VALUES(api_version), title = VALUES(title), "
            + "parent = VALUES(parent), owner = VALUES(owner), updated_at = VALUES(updated_at);";

        var parameters = rows.Select(r => new { r.Id, r.Type, r.ApiVersion, r.Title, r.Parent, r.Owner, Now = now });
        await connection.ExecuteAsync(new CommandDefinition(sql, parameters, transaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
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
            r.Id,
            r.ApiVersion,
            r.Title,
            r.Organisation,
            r.Standard,
            r.Disposition,
            Now = now,
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
            r.Id,
            r.ApiVersion,
            r.Title,
            r.Organisation,
            r.Requirement,
            r.Disposition,
            Now = now,
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
            r.Id,
            r.ApiVersion,
            r.Title,
            r.Vendor,
            r.Requirement,
            r.Control,
            r.Disposition,
            r.Justification,
            Now = now,
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

    private static async Task DeleteAbsentDeclaredAssetsAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyList<string> keepIds,
        CancellationToken cancellationToken)
    {
        // Guarded by source = 'declared' so a config with zero declared assets never truncates the
        // discovered inventory. assets.parent has no FK, so a parent can be deleted while a child
        // survives (the child is left with a tolerated dangling parent); no leaf-first loop is needed.
        var sql = keepIds.Count == 0
            ? "DELETE FROM assets WHERE source = 'declared';"
            : "DELETE FROM assets WHERE source = 'declared' AND id NOT IN @KeepIds;";

        await connection.ExecuteAsync(new CommandDefinition(
            sql, new { KeepIds = keepIds }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
