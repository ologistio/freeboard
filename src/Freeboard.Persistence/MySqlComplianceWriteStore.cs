using System.Data.Common;
using Dapper;
using Freeboard.Core.GitOps;
using MySqlConnector;

namespace Freeboard.Persistence;

/// <summary>
/// MySQL-backed <see cref="IComplianceWriteStore"/>. Organisations are declared Company/Department rows
/// in the unified assets table. Each write runs in one transaction and checks the domain invariants
/// against the current rows before committing; a violated invariant rolls back so the store is never left
/// in a half-written state. These app-managed writes stay STRICTER than the gitops sync path: a
/// self-parent, a cycle, a dangling parent, or deleting an org with children or scopes is rejected here as
/// an immediate authoring error, where sync only warns. assets.parent carries no foreign key, so these
/// app-level guards (not a DB self-FK) are what hold the org tree acyclic and referentially whole; the
/// unique <c>(organisation_id, standard_id)</c> and <c>(organisation_id, requirement_id)</c> scope keys
/// remain the DB backstop.
/// </summary>
public sealed class MySqlComplianceWriteStore(IDbConnectionFactory connectionFactory) : IComplianceWriteStore
{
    public async Task<WriteResult> UpsertOrganisationAsync(
        string id,
        string title,
        string kind,
        string? parent,
        bool expectExisting = false,
        string? expectedCurrentParent = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return WriteResult.Fail("Organisation id is required.");
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return WriteResult.Fail("Organisation title is required.");
        }

        if (!ConfigValidator.TryParseKind(kind, out _))
        {
            return WriteResult.Fail($"Organisation kind must be '{nameof(OrganisationKind.Company)}' or '{nameof(OrganisationKind.Department)}'.");
        }

        var parentId = string.IsNullOrEmpty(parent) ? null : parent;
        var expectedParentId = string.IsNullOrEmpty(expectedCurrentParent) ? null : expectedCurrentParent;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (await LockedParentChangedAsync(connection, transaction, id, expectExisting, expectedParentId, cancellationToken).ConfigureAwait(false))
        {
            return WriteResult.Conflict("The organisation's parent changed concurrently; re-authorize and retry.");
        }

        if (parentId is not null)
        {
            if (string.Equals(parentId, id, StringComparison.Ordinal))
            {
                return WriteResult.Fail("An organisation cannot be its own parent.");
            }

            var parentExists = await OrganisationExistsAsync(connection, transaction, parentId, cancellationToken).ConfigureAwait(false);
            if (!parentExists)
            {
                return WriteResult.Fail($"Parent organisation '{parentId}' does not exist.");
            }

            if (await WouldFormCycleAsync(connection, transaction, id, parentId, cancellationToken).ConfigureAwait(false))
            {
                return WriteResult.Fail("Setting this parent would form an organisation cycle.");
            }
        }

        var now = DateTime.UtcNow;
        var parameters = new { Id = id, ApiVersion = GitOpsSchema.ApiVersion, Title = title, Kind = kind, Parent = parentId, Now = now };
        try
        {
            // Org rows now live in the unified assets table as declared Company/Department assets; the
            // org kind is the asset `type` and the parent is `parent`. Create is INSERT-only so a row
            // inserted concurrently (or an id already used by any asset) surfaces as a conflict, not a
            // silent overwrite. Update stays an upsert keyed on the row the caller re-locked above.
            var sql = expectExisting
                ? "INSERT INTO assets (id, type, source, api_version, title, parent, created_at, updated_at) "
                    + "VALUES (@Id, @Kind, 'declared', @ApiVersion, @Title, @Parent, @Now, @Now) "
                    + "ON DUPLICATE KEY UPDATE "
                    + "api_version = VALUES(api_version), title = VALUES(title), type = VALUES(type), "
                    + "parent = VALUES(parent), updated_at = VALUES(updated_at);"
                : "INSERT INTO assets (id, type, source, api_version, title, parent, created_at, updated_at) "
                    + "VALUES (@Id, @Kind, 'declared', @ApiVersion, @Title, @Parent, @Now, @Now);";
            await connection.ExecuteAsync(new CommandDefinition(
                sql, parameters, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            return WriteResult.Conflict("An organisation with that id already exists.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return WriteResult.Success;
    }

    public async Task<WriteResult> DeleteOrganisationAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var childCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM assets WHERE parent = @Id AND type IN ('Company', 'Department');",
            new { Id = id }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (childCount > 0)
        {
            return WriteResult.Fail("Cannot delete an organisation that still has child organisations.");
        }

        var scopeCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM scopes WHERE organisation_id = @Id;",
            new { Id = id }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (scopeCount > 0)
        {
            return WriteResult.Fail("Cannot delete an organisation that still has scopes.");
        }

        var requirementScopeCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM requirement_scopes WHERE organisation_id = @Id;",
            new { Id = id }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (requirementScopeCount > 0)
        {
            return WriteResult.Fail("Cannot delete an organisation that still has requirement-scopes.");
        }

        // Prune the org's role assignments before the delete: the organisation FK is ON DELETE
        // RESTRICT, so an existing assignment would otherwise wedge the delete. Same prune-before-
        // delete pattern the pre-counts above rely on for scopes.
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM authz_organisation_role_assignments WHERE organisation_id = @Id;",
            new { Id = id }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM assets WHERE id = @Id AND type IN ('Company', 'Department');",
            new { Id = id }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return WriteResult.Success;
    }

    public async Task<WriteResult> UpsertScopeDispositionAsync(
        string id,
        string title,
        string organisation,
        string standard,
        string disposition,
        string? expectedCurrentOrganisation = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return WriteResult.Fail("Scope id is required.");
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return WriteResult.Fail("Scope title is required.");
        }

        if (!ConfigValidator.TryParseDisposition(disposition, out _))
        {
            return WriteResult.Fail($"Disposition must be '{nameof(ScopeDisposition.In)}' or '{nameof(ScopeDisposition.Out)}'.");
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (await LockedOwnerChangedAsync(connection, transaction, "scopes", id, expectedCurrentOrganisation, cancellationToken).ConfigureAwait(false))
        {
            return WriteResult.Conflict("The scope's owning organisation changed concurrently; re-authorize and retry.");
        }

        if (!await OrganisationExistsAsync(connection, transaction, organisation, cancellationToken).ConfigureAwait(false))
        {
            return WriteResult.Fail($"Organisation '{organisation}' does not exist.");
        }

        if (!await ExistsAsync(connection, transaction, "standards", standard, cancellationToken).ConfigureAwait(false))
        {
            return WriteResult.Fail($"Standard '{standard}' does not exist.");
        }

        // At most one scope per (organisation, standard). A row for the pair under a DIFFERENT
        // id is a duplicate mapping; the same id updating its own pair is fine.
        var conflictingId = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT id FROM scopes WHERE organisation_id = @Organisation AND standard_id = @Standard LIMIT 1;",
            new { Organisation = organisation, Standard = standard }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (conflictingId is not null && !string.Equals(conflictingId, id, StringComparison.Ordinal))
        {
            return WriteResult.Fail(
                $"A scope already maps organisation '{organisation}' to standard '{standard}'.");
        }

        var now = DateTime.UtcNow;
        var parameters = new
        {
            Id = id,
            ApiVersion = GitOpsSchema.ApiVersion,
            Title = title,
            Organisation = organisation,
            Standard = standard,
            Disposition = disposition,
            Now = now,
        };
        try
        {
            // A null expected owner is a create: INSERT-only so a row inserted concurrently between the
            // lock and this write conflicts rather than silently overwriting. An expected owner is an
            // update on the row the caller was authorized for and already re-locked above.
            var isCreate = expectedCurrentOrganisation is null;
            var sql = isCreate
                ? "INSERT INTO scopes (id, api_version, title, organisation_id, standard_id, disposition, created_at, updated_at) "
                    + "VALUES (@Id, @ApiVersion, @Title, @Organisation, @Standard, @Disposition, @Now, @Now);"
                : "INSERT INTO scopes (id, api_version, title, organisation_id, standard_id, disposition, created_at, updated_at) "
                    + "VALUES (@Id, @ApiVersion, @Title, @Organisation, @Standard, @Disposition, @Now, @Now) "
                    + "ON DUPLICATE KEY UPDATE "
                    + "api_version = VALUES(api_version), title = VALUES(title), organisation_id = VALUES(organisation_id), "
                    + "standard_id = VALUES(standard_id), disposition = VALUES(disposition), updated_at = VALUES(updated_at);";
            await connection.ExecuteAsync(new CommandDefinition(
                sql, parameters, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            return WriteResult.Conflict("A scope with that id already exists.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return WriteResult.Success;
    }

    public async Task<WriteResult> DeleteScopeAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM scopes WHERE id = @Id;",
            new { Id = id }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return WriteResult.Success;
    }

    public async Task<WriteResult> UpsertRequirementScopeDispositionAsync(
        string id,
        string title,
        string organisation,
        string requirement,
        string disposition,
        string? expectedCurrentOrganisation = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return WriteResult.Fail("RequirementScope id is required.");
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return WriteResult.Fail("RequirementScope title is required.");
        }

        if (!ConfigValidator.TryParseDisposition(disposition, out _))
        {
            return WriteResult.Fail($"Disposition must be '{nameof(ScopeDisposition.In)}' or '{nameof(ScopeDisposition.Out)}'.");
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (await LockedOwnerChangedAsync(connection, transaction, "requirement_scopes", id, expectedCurrentOrganisation, cancellationToken).ConfigureAwait(false))
        {
            return WriteResult.Conflict("The requirement-scope's owning organisation changed concurrently; re-authorize and retry.");
        }

        if (!await OrganisationExistsAsync(connection, transaction, organisation, cancellationToken).ConfigureAwait(false))
        {
            return WriteResult.Fail($"Organisation '{organisation}' does not exist.");
        }

        if (!await ExistsAsync(connection, transaction, "requirements", requirement, cancellationToken).ConfigureAwait(false))
        {
            return WriteResult.Fail($"Requirement '{requirement}' does not exist.");
        }

        // At most one requirement-scope per (organisation, requirement). A row for the pair under a
        // DIFFERENT id is a duplicate mapping; the same id updating its own pair is fine.
        var conflictingId = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT id FROM requirement_scopes WHERE organisation_id = @Organisation AND requirement_id = @Requirement LIMIT 1;",
            new { Organisation = organisation, Requirement = requirement }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (conflictingId is not null && !string.Equals(conflictingId, id, StringComparison.Ordinal))
        {
            return WriteResult.Fail(
                $"A requirement-scope already maps organisation '{organisation}' to requirement '{requirement}'.");
        }

        var now = DateTime.UtcNow;
        var parameters = new
        {
            Id = id,
            ApiVersion = GitOpsSchema.ApiVersion,
            Title = title,
            Organisation = organisation,
            Requirement = requirement,
            Disposition = disposition,
            Now = now,
        };
        try
        {
            // A null expected owner is a create: INSERT-only so a row inserted concurrently between the
            // lock and this write conflicts rather than silently overwriting. An expected owner is an
            // update on the row the caller was authorized for and already re-locked above.
            var isCreate = expectedCurrentOrganisation is null;
            var sql = isCreate
                ? "INSERT INTO requirement_scopes (id, api_version, title, organisation_id, requirement_id, disposition, created_at, updated_at) "
                    + "VALUES (@Id, @ApiVersion, @Title, @Organisation, @Requirement, @Disposition, @Now, @Now);"
                : "INSERT INTO requirement_scopes (id, api_version, title, organisation_id, requirement_id, disposition, created_at, updated_at) "
                    + "VALUES (@Id, @ApiVersion, @Title, @Organisation, @Requirement, @Disposition, @Now, @Now) "
                    + "ON DUPLICATE KEY UPDATE "
                    + "api_version = VALUES(api_version), title = VALUES(title), organisation_id = VALUES(organisation_id), "
                    + "requirement_id = VALUES(requirement_id), disposition = VALUES(disposition), updated_at = VALUES(updated_at);";
            await connection.ExecuteAsync(new CommandDefinition(
                sql, parameters, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            return WriteResult.Conflict("A requirement-scope with that id already exists.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return WriteResult.Success;
    }

    public async Task<WriteResult> DeleteRequirementScopeAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM requirement_scopes WHERE id = @Id;",
            new { Id = id }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return WriteResult.Success;
    }

    private static async Task<bool> ExistsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        string id,
        CancellationToken cancellationToken)
    {
        var count = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COUNT(*) FROM {table} WHERE id = @Id;",
            new { Id = id }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return count > 0;
    }

    /// <summary>
    /// True when the id names a Company or Department asset. Org data now lives in the unified assets
    /// table, so an org-existence check must exclude Vendor and Machine rows sharing the id space.
    /// </summary>
    private static async Task<bool> OrganisationExistsAsync(
        DbConnection connection, DbTransaction transaction, string id, CancellationToken cancellationToken)
    {
        var count = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM assets WHERE id = @Id AND type IN ('Company', 'Department');",
            new { Id = id }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return count > 0;
    }

    /// <summary>
    /// Locks the existing row (<c>SELECT ... FOR UPDATE</c>) and reports whether its current owning
    /// organisation differs from the one the caller authorized. When the caller expected no existing row
    /// (<paramref name="expectedCurrentOrganisation"/> null), a row that now exists under any org is a
    /// change. A row absent under the lock is not a change: the upsert then inserts it afresh under the
    /// requested org, which the caller already authorized. Closes the cross-org-move TOCTOU: the
    /// authorized current owner cannot change between the pre-write authorization and the write.
    /// The table name is a fixed internal constant, never caller input.
    /// </summary>
    private static async Task<bool> LockedOwnerChangedAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        string id,
        string? expectedCurrentOrganisation,
        CancellationToken cancellationToken)
    {
        var currentOwner = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            $"SELECT organisation_id FROM {table} WHERE id = @Id FOR UPDATE;",
            new { Id = id }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return currentOwner is not null
            && !string.Equals(currentOwner, expectedCurrentOrganisation, StringComparison.Ordinal);
    }

    /// <summary>
    /// Locks the organisation row (<c>SELECT ... FOR UPDATE</c>) and reports whether its current parent
    /// differs from the one the caller authorized. On an update (<paramref name="expectExisting"/> true) a
    /// row absent under the lock lost a concurrent delete (a change), and a present row is a change only
    /// when its locked parent differs from <paramref name="expectedCurrentParent"/>. On a create
    /// (<paramref name="expectExisting"/> false) a row now present is a concurrent create (a change) and an
    /// absent row is inserted fresh under the authorized parent. A null parent is a legitimate root, so the
    /// caller passes <paramref name="expectExisting"/> explicitly rather than overloading the null.
    /// Closes the cross-parent-move TOCTOU: the authorized current parent cannot change between the
    /// pre-write authorization and the write.
    /// </summary>
    private static async Task<bool> LockedParentChangedAsync(
        DbConnection connection,
        DbTransaction transaction,
        string id,
        bool expectExisting,
        string? expectedCurrentParent,
        CancellationToken cancellationToken)
    {
        var rows = (await connection.QueryAsync<string?>(new CommandDefinition(
            "SELECT parent FROM assets WHERE id = @Id AND type IN ('Company', 'Department') FOR UPDATE;",
            new { Id = id }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        if (rows.Count == 0)
        {
            return expectExisting;
        }

        return !expectExisting
            || !string.Equals(rows[0], expectedCurrentParent, StringComparison.Ordinal);
    }

    /// <summary>
    /// True if making <paramref name="parentId"/> the parent of <paramref name="childId"/> would
    /// create a cycle: it does when <paramref name="childId"/> is already an ancestor of
    /// <paramref name="parentId"/> (so the new edge would close a loop).
    /// </summary>
    private static async Task<bool> WouldFormCycleAsync(
        DbConnection connection,
        DbTransaction transaction,
        string childId,
        string parentId,
        CancellationToken cancellationToken)
    {
        var current = (string?)parentId;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (current is not null && visited.Add(current))
        {
            if (string.Equals(current, childId, StringComparison.Ordinal))
            {
                return true;
            }

            current = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT parent FROM assets WHERE id = @Id AND type IN ('Company', 'Department');",
                new { Id = current }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        return false;
    }
}
