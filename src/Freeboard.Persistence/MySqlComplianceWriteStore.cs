using System.Data.Common;
using Dapper;
using Freeboard.Core.Assets;
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
/// unified scopes table's <c>(subject_id, standard_id)</c> and <c>(subject_id, requirement_id)</c> unique
/// keys remain the DB backstop.
///
/// The <c>type IN ('Company', 'Department')</c> predicates throughout are DEFENCE IN DEPTH, not the thing
/// holding the authorization line: an unauthorized caller is refused before this store is reached. They
/// stay because a caller arriving by another path still has to meet them, but a write that leans on them
/// would answer an authorization failure as a 404 or a no-op, neither of which is auditable as a denial.
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

        if (!ConfigValidator.TryParseAssetType(kind, out var assetType)
            || assetType is not (AssetKind.Company or AssetKind.Department))
        {
            return WriteResult.Fail($"Organisation kind must be '{nameof(AssetKind.Company)}' or '{nameof(AssetKind.Department)}'.");
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

        // One unified scopes table now; an org subject may target a standard, requirement, or control, so
        // the guard counts any scope whose subject is this org.
        var scopeCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM scopes WHERE subject_id = @Id;",
            new { Id = id }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (scopeCount > 0)
        {
            return WriteResult.Fail("Cannot delete an organisation that still has scopes.");
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

    public Task<WriteResult> UpsertScopeDispositionAsync(
        string id,
        string title,
        string subject,
        string standard,
        string disposition,
        string? justification = null,
        string? expectedCurrentOrganisation = null,
        CancellationToken cancellationToken = default) =>
        UpsertScopeTargetAsync(
            ScopeTarget.Standard, id, title, subject, "standards", standard, disposition, justification,
            expectedCurrentOrganisation, cancellationToken);

    public Task<WriteResult> DeleteScopeAsync(string id, string expectedOwner, CancellationToken cancellationToken = default) =>
        DeleteScopeTargetAsync(ScopeTarget.Standard, id, expectedOwner, cancellationToken);

    public Task<WriteResult> UpsertRequirementScopeDispositionAsync(
        string id,
        string title,
        string subject,
        string requirement,
        string disposition,
        string? justification = null,
        string? expectedCurrentOrganisation = null,
        CancellationToken cancellationToken = default) =>
        UpsertScopeTargetAsync(
            ScopeTarget.Requirement, id, title, subject, "requirements", requirement, disposition, justification,
            expectedCurrentOrganisation, cancellationToken);

    public Task<WriteResult> DeleteRequirementScopeAsync(string id, string expectedOwner, CancellationToken cancellationToken = default) =>
        DeleteScopeTargetAsync(ScopeTarget.Requirement, id, expectedOwner, cancellationToken);

    /// <summary>The app-writable target columns of the unified scopes table. Control targets are
    /// gitops-write-only and unreachable from either app route.</summary>
    private enum ScopeTarget
    {
        Standard,
        Requirement,
    }

    // The one disposition upsert shared by both app routes, confined to the route's own target column so an
    // id cannot cross the target boundary (an authz boundary). It branches on the GLOBAL id (not target
    // filtered): no row = create a row of this target kind; a same-target-kind row = update; a
    // different-target-kind row = not-found (a PUT must not convert a row's target kind).
    private async Task<WriteResult> UpsertScopeTargetAsync(
        ScopeTarget target,
        string id,
        string title,
        string subject,
        string targetTable,
        string targetId,
        string disposition,
        string? justification,
        string? expectedCurrentOrganisation,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return WriteResult.Fail("Scope id is required.");
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return WriteResult.Fail("Scope title is required.");
        }

        if (!ConfigValidator.TryParseDisposition(disposition, out var parsedDisposition))
        {
            return WriteResult.Fail($"Disposition must be '{nameof(ScopeDisposition.In)}' or '{nameof(ScopeDisposition.Out)}'.");
        }

        var normalizedJustification = string.IsNullOrWhiteSpace(justification) ? null : justification;
        if (parsedDisposition == ScopeDisposition.Out && normalizedJustification is null)
        {
            return WriteResult.Fail("A scope with disposition 'Out' requires a justification.");
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Lock the row by global id and read its target columns and subject, joining the subject asset for
        // its type. A row whose own target column is not this route's kind is not-found: a PUT must not
        // retarget it, and it must not be authorized against that row's owning subject. A row whose subject
        // is not a live Company/Department asset (a Vendor/Machine subject, or an unresolved subject) is
        // likewise not-found: those scopes are GitOps-write-only, so an app PUT must not retarget them.
        var locked = (await connection.QueryAsync<ScopeLockRow>(new CommandDefinition(
            "SELECT s.standard_id AS StandardId, s.requirement_id AS RequirementId, s.control_id AS ControlId, "
            + "s.subject_id AS SubjectId, a.type AS SubjectType FROM scopes s "
            + "LEFT JOIN assets a ON a.id = s.subject_id WHERE s.id = @Id FOR UPDATE;",
            new { Id = id }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).FirstOrDefault();

        if (locked is not null)
        {
            var thisKindId = target == ScopeTarget.Standard ? locked.StandardId : locked.RequirementId;
            if (thisKindId is null)
            {
                return WriteResult.NotFound();
            }

            if (locked.SubjectType is not ("Company" or "Department"))
            {
                return WriteResult.NotFound();
            }

            if (expectedCurrentOrganisation is null)
            {
                // The caller authorized a create, but a same-kind row now exists: a concurrent create.
                return WriteResult.Conflict("A scope with that id already exists.");
            }

            if (!string.Equals(locked.SubjectId, expectedCurrentOrganisation, StringComparison.Ordinal))
            {
                return WriteResult.Conflict("The scope's owning organisation changed concurrently; re-authorize and retry.");
            }
        }

        if (!await OrganisationExistsAsync(connection, transaction, subject, cancellationToken).ConfigureAwait(false))
        {
            return WriteResult.Fail($"Organisation '{subject}' does not exist.");
        }

        if (!await ExistsAsync(connection, transaction, targetTable, targetId, cancellationToken).ConfigureAwait(false))
        {
            return WriteResult.Fail($"Target '{targetId}' does not exist.");
        }

        // At most one scope per (subject, target). A row for the pair under a DIFFERENT id is a duplicate
        // mapping; the same id updating its own pair is fine. The pair query is confined to this target
        // column, so a wrong-kind row (target column null) never matches.
        var pairColumn = target == ScopeTarget.Standard ? "standard_id" : "requirement_id";
        var conflictingId = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            $"SELECT id FROM scopes WHERE subject_id = @Subject AND {pairColumn} = @TargetId LIMIT 1;",
            new { Subject = subject, TargetId = targetId }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (conflictingId is not null && !string.Equals(conflictingId, id, StringComparison.Ordinal))
        {
            return WriteResult.Fail($"A scope already maps subject '{subject}' to target '{targetId}'.");
        }

        var now = DateTime.UtcNow;
        var parameters = new
        {
            Id = id,
            ApiVersion = GitOpsSchema.ApiVersion,
            Title = title,
            Subject = subject,
            Standard = target == ScopeTarget.Standard ? targetId : null,
            Requirement = target == ScopeTarget.Requirement ? targetId : null,
            Disposition = disposition,
            Justification = normalizedJustification,
            Now = now,
        };
        try
        {
            // A null expected owner is a create: INSERT-only so a row inserted concurrently between the
            // lock and this write conflicts rather than silently overwriting. An expected owner is an
            // update on the row the caller was authorized for and already re-locked above. Both write the
            // route's own target column and leave the other two target columns null.
            var isCreate = expectedCurrentOrganisation is null;
            var sql = isCreate
                ? "INSERT INTO scopes (id, api_version, title, subject_id, standard_id, requirement_id, control_id, disposition, justification, created_at, updated_at) "
                    + "VALUES (@Id, @ApiVersion, @Title, @Subject, @Standard, @Requirement, NULL, @Disposition, @Justification, @Now, @Now);"
                : "INSERT INTO scopes (id, api_version, title, subject_id, standard_id, requirement_id, control_id, disposition, justification, created_at, updated_at) "
                    + "VALUES (@Id, @ApiVersion, @Title, @Subject, @Standard, @Requirement, NULL, @Disposition, @Justification, @Now, @Now) "
                    + "ON DUPLICATE KEY UPDATE "
                    + "api_version = VALUES(api_version), title = VALUES(title), subject_id = VALUES(subject_id), "
                    + "standard_id = VALUES(standard_id), requirement_id = VALUES(requirement_id), "
                    + "disposition = VALUES(disposition), justification = VALUES(justification), updated_at = VALUES(updated_at);";
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

    // Target-column-scoped delete, further confined to a Company/Department subject: the standard route
    // deletes only a standard-target row, the requirement route only a requirement-target row, and either
    // only when the subject is a live Company/Department asset. A wrong-kind id (or a control-target row),
    // and a Vendor/Machine or unresolved subject (those scopes are GitOps-write-only), match no row and are
    // not-found. Never an unqualified DELETE FROM scopes WHERE id=@Id (that would be an authz-boundary
    // bypass letting one permission delete the other route's rows).
    //
    // The delete always also matches the row's current subject against expectedOwner, so it removes at most
    // the row the caller was authorized against. The owner predicate is unconditional (fail closed): there
    // is no null-owner path that would restore an unbound delete. A row concurrently reparented to another
    // organisation no longer matches and is left untouched (not-found), closing the same cross-org-move
    // TOCTOU the PUT closes under its FOR UPDATE re-check.
    private async Task<WriteResult> DeleteScopeTargetAsync(
        ScopeTarget target, string id, string expectedOwner, CancellationToken cancellationToken)
    {
        var column = target == ScopeTarget.Standard ? "standard_id" : "requirement_id";
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            $"DELETE s FROM scopes s JOIN assets a ON a.id = s.subject_id "
            + $"WHERE s.id = @Id AND s.{column} IS NOT NULL AND a.type IN ('Company', 'Department') "
            + "AND s.subject_id = @ExpectedOwner;",
            new { Id = id, ExpectedOwner = expectedOwner }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return affected == 0 ? WriteResult.NotFound() : WriteResult.Success;
    }

    /// <summary>The target columns, subject, and subject asset type of a locked scope row, read by global
    /// id. <see cref="SubjectType"/> is null when the subject resolves to no asset row.</summary>
    private sealed record ScopeLockRow(
        string? StandardId, string? RequirementId, string? ControlId, string? SubjectId, string? SubjectType);

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
