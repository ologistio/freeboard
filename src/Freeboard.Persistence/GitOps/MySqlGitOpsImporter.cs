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
/// vendor assets), collectors (reference controls, vendor assets, and integration-connections);
/// replace the whole unified scope set (delete-all then insert,
/// one table with a scalar subject and three nullable target FKs); replace all control->requirement join
/// rows; replace the whole vendor assurance set, which must precede the declared-asset prune, and that
/// prune itself precedes the standard delete, so one placement satisfies both of its RESTRICT FKs;
/// then hard-remove absent rows: org role assignments, absent collectors,
/// absent integration-connections, then ONE source = 'declared'-guarded
/// declared-asset prune (which never touches a discovered row) after every asset-referencing row is gone,
/// then controls, requirements before standards. Finally, before commit, compute the DB-accurate
/// unresolved-scope-subject set and return it. Matches on id only.
/// </summary>
public sealed class MySqlGitOpsImporter(IDbConnectionFactory connectionFactory) : IGitOpsImporter
{
    public async Task<ImportResult> ImportAsync(GitOpsConfig config, CancellationToken cancellationToken = default)
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
        // has no FK, so no parent-before-child order is needed. Their referencing scopes and collectors are
        // replaced/pruned below; absent declared assets are pruned after those, in one source-guarded prune
        // that never touches a discovered row.
        await UpsertAssetsAsync(connection, transaction, plan.Assets, now, cancellationToken).ConfigureAwait(false);

        // Integration-connections reference vendors, so upsert them after vendors and before the
        // collectors that reference them. Absent connections are pruned after absent collectors
        // and before absent vendors in step 6, keeping both RESTRICT FKs safe.
        await UpsertIntegrationConnectionsAsync(
            connection, transaction, plan.IntegrationConnections, now, cancellationToken).ConfigureAwait(false);

        // Collectors reference controls, vendors, and integration-connections, so upsert them
        // after all three. Upsert by id (no secondary unique key); absent collectors are pruned before
        // their target rows in step 6.
        await UpsertCollectorsAsync(
            connection, transaction, plan.Collectors, now, cancellationToken).ConfigureAwait(false);

        // 2. Replace the whole unified scope set (delete-all then insert). A plain upsert is unsafe: the
        //    table has a primary key (id) plus three (subject, target) unique keys, so a row that swaps
        //    pairs while keeping its id can match a pair key under ON DUPLICATE KEY UPDATE and update the
        //    wrong row. Nothing references scopes, so a whole-set replace is safe and subsumes the
        //    absent-row prune. It runs before the absent-standard/requirement/control deletes (the target
        //    FKs are RESTRICT, so a referencing scope must go first) and before the declared-asset prune;
        //    subject_id has no FK, so a removed subject asset simply leaves the scope dangling (a tolerated
        //    warning), never blocking the prune.
        await ReplaceScopesAsync(connection, transaction, plan.Scopes, now, cancellationToken).ConfigureAwait(false);

        // 3. Replace all control->requirement join rows for the imported set (whole-set delete+insert).
        await ReplaceControlRequirementsAsync(connection, transaction, plan, cancellationToken).ConfigureAwait(false);

        // Replace the whole vendor assurance set (delete-all then insert). An upsert would leave behind
        // the row of an entry the author removed, and a per-vendor replace would additionally miss the
        // rows of a vendor that left the config entirely. Both of its FKs (vendor_id, standard_id) are
        // RESTRICT, and one placement covers both: it must precede the declared-asset prune, which itself
        // already precedes the absent-standard delete.
        await ReplaceVendorAssurancesAsync(connection, transaction, plan.VendorAssurances, now, cancellationToken)
            .ConfigureAwait(false);

        // 4. Prune org-scoped role assignments for absent organisations before the org delete: the
        //    organisation FK is ON DELETE RESTRICT, so a stale assignment would wedge the delete. The
        //    importer needs no role semantics, only the prune, mirroring how it prunes absent scopes.
        await DeleteAbsentOrganisationAssignmentsAsync(connection, transaction, plan.OrganisationIds, cancellationToken).ConfigureAwait(false);

        // 5. Hard-remove remaining rows whose id is absent, FK-safe order. Prune absent collectors
        //    before their target rows: the collector FKs to controls and vendor assets are RESTRICT, so a
        //    still-referenced control or vendor asset cannot be deleted while a stale collector points at it.
        //    A pruned collector's credentials cascade away with it.
        await DeleteAbsentAsync(connection, transaction, "collectors", plan.CollectorIds, cancellationToken).ConfigureAwait(false);
        // Prune absent integration_connections after absent collectors (whose connection_id FK
        // is RESTRICT) and before the declared-asset prune (the connection's vendor_id FK to a vendor
        // asset is RESTRICT), so both FKs stay satisfied.
        await DeleteAbsentAsync(connection, transaction, "integration_connections", plan.IntegrationConnectionIds, cancellationToken).ConfigureAwait(false);
        // The single declared-asset prune, guarded by source = 'declared' so it NEVER touches a discovered
        // row. It runs after every row that references an asset (scopes' target FKs, collectors,
        // integration_connections, and the org role assignments) has been pruned or replaced to only
        // reference in-config assets, so the RESTRICT FKs into assets stay satisfied. A scope's subject_id
        // has no FK, so a removed subject asset simply leaves the scope dangling and never blocks the prune.
        // assets.parent has no FK, so removing a parent while a child survives is a tolerated dangling
        // edge, not an FK violation - no child-before-parent order is needed.
        await DeleteAbsentDeclaredAssetsAsync(connection, transaction, plan.AssetIds, cancellationToken).ConfigureAwait(false);
        await DeleteAbsentAsync(connection, transaction, "controls", plan.ControlIds, cancellationToken).ConfigureAwait(false);
        await DeleteAbsentAsync(connection, transaction, "requirements", plan.RequirementIds, cancellationToken).ConfigureAwait(false);
        await DeleteAbsentAsync(connection, transaction, "standards", plan.StandardIds, cancellationToken).ConfigureAwait(false);

        // After all writes but BEFORE commit, compute the DB-accurate unresolved-subject set against the
        // final post-write asset state, inside the same transaction. Running it here (not post-commit)
        // preserves all-or-nothing: a query failure rolls the whole import back rather than leaving a
        // committed import whose caller then throws. Only the DB sees discovered and retired Machine
        // subjects, so this is the authoritative signal Core (which has no database) cannot produce.
        var unresolvedSubjects =
            await FindUnresolvedScopeSubjectsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ImportResult(unresolvedSubjects);
    }

    // The subject-resolution predicate: a subject is unresolved when NO assets row has its id OR the row is
    // a discovered asset in the Retired state (a retired discovered machine keeps its row yet is not a live
    // authorization anchor). Distinct so a subject shared by several scopes is reported once.
    private static async Task<IReadOnlyList<string>> FindUnresolvedScopeSubjectsAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql =
            "SELECT DISTINCT s.subject_id FROM scopes s "
            + "LEFT JOIN assets a ON a.id = s.subject_id "
            + "WHERE a.id IS NULL OR (a.source = 'discovered' AND a.state = 'Retired') "
            + "ORDER BY s.subject_id;";

        var rows = await connection.QueryAsync<string>(new CommandDefinition(
            sql, transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToList();
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

    private static async Task UpsertCollectorsAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyList<CollectorRowPlan> rows,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }

        // Upsert by id (identity is id only, no secondary unique key). ConfigJson is written straight
        // into the native JSON column, which validates well-formedness.
        const string sql =
            "INSERT INTO collectors "
            + "(id, api_version, title, control_id, vendor_id, connection_id, type, provider, frequency, threshold, config, created_at, updated_at) "
            + "VALUES (@Id, @ApiVersion, @Title, @Control, @Vendor, @Connection, @Type, @Provider, @Frequency, @Threshold, @ConfigJson, @Now, @Now) "
            + "ON DUPLICATE KEY UPDATE "
            + "api_version = VALUES(api_version), title = VALUES(title), control_id = VALUES(control_id), "
            + "vendor_id = VALUES(vendor_id), connection_id = VALUES(connection_id), type = VALUES(type), "
            + "provider = VALUES(provider), "
            + "frequency = VALUES(frequency), threshold = VALUES(threshold), config = VALUES(config), "
            + "updated_at = VALUES(updated_at);";

        var parameters = rows.Select(r => new
        {
            r.Id,
            r.ApiVersion,
            r.Title,
            r.Control,
            r.Vendor,
            r.Connection,
            r.Type,
            r.Provider,
            r.Frequency,
            r.Threshold,
            r.ConfigJson,
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
        //
        // Every mutable column is assigned from VALUES on duplicate key, so removing a key from config
        // clears the column rather than leaving the previous value behind. Miss one here and a stale tier
        // or data class list survives an edit with no diagnostic anywhere.
        const string sql =
            "INSERT INTO assets (id, type, source, api_version, title, parent, owner, tier, data_classes, created_at, updated_at) "
            + "VALUES (@Id, @Type, 'declared', @ApiVersion, @Title, @Parent, @Owner, @Tier, @DataClasses, @Now, @Now) "
            + "ON DUPLICATE KEY UPDATE "
            + "type = VALUES(type), api_version = VALUES(api_version), title = VALUES(title), "
            + "parent = VALUES(parent), owner = VALUES(owner), tier = VALUES(tier), "
            + "data_classes = VALUES(data_classes), updated_at = VALUES(updated_at);";

        var parameters = rows.Select(r => new
        {
            r.Id,
            r.Type,
            r.ApiVersion,
            r.Title,
            r.Parent,
            r.Owner,
            r.Tier,
            r.DataClasses,
            Now = now,
        });
        await connection.ExecuteAsync(new CommandDefinition(sql, parameters, transaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    private static async Task ReplaceScopesAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyList<ScopeRowPlan> rows,
        DateTime now,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM scopes;", transaction: transaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        if (rows.Count == 0)
        {
            return;
        }

        // Plain INSERT after the delete: config validation guarantees unique ids and unique
        // (subject, standard) / (subject, requirement) / (subject, control) pairs, so no duplicate key can
        // arise. Exactly one target column is non-null per row (the CHECK). subject_id has no FK.
        const string sql =
            "INSERT INTO scopes "
            + "(id, api_version, title, subject_id, standard_id, requirement_id, control_id, disposition, justification, created_at, updated_at) "
            + "VALUES (@Id, @ApiVersion, @Title, @Subject, @Standard, @Requirement, @Control, @Disposition, @Justification, @Now, @Now);";

        var parameters = rows.Select(r => new
        {
            r.Id,
            r.ApiVersion,
            r.Title,
            r.Subject,
            r.Standard,
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

    private static async Task ReplaceVendorAssurancesAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyList<VendorAssuranceRowPlan> rows,
        DateTime now,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM vendor_assurances;", transaction: transaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        if (rows.Count == 0)
        {
            return;
        }

        // Plain INSERT after the delete: config validation guarantees one entry per (vendor, standard),
        // so no duplicate key can arise.
        const string sql =
            "INSERT INTO vendor_assurances (vendor_id, standard_id, expires, warn_days, created_at, updated_at) "
            + "VALUES (@VendorId, @StandardId, @Expires, @WarnDays, @Now, @Now);";

        var parameters = rows.Select(r => new { r.VendorId, r.StandardId, r.Expires, r.WarnDays, Now = now });
        await connection.ExecuteAsync(new CommandDefinition(sql, parameters, transaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
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
