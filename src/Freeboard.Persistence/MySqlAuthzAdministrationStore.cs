using System.Data;
using System.Data.Common;
using Dapper;
using Freeboard.Core.Authz;
using Freeboard.Persistence.Auth;
using MySqlConnector;

namespace Freeboard.Persistence;

/// <summary>
/// MySQL-backed <see cref="IAuthzAdministrationStore"/>. Assign validates the role/user/org and the
/// role scope before inserting; the unique key backstops a concurrent duplicate (mapped to a
/// conflict). Revoke enforces the last-super-admin and last-owner guards atomically: it locks the
/// relevant assignment rows with <c>SELECT ... FOR UPDATE</c> inside the same transaction that then
/// deletes, so two concurrent revokes (or a revoke racing a disable) cannot both zero out the last
/// usable administrator.
/// </summary>
public sealed class MySqlAuthzAdministrationStore(IDbConnectionFactory connectionFactory, IUlidFactory ulidFactory)
    : IAuthzAdministrationStore
{
    public async Task<AuthzWriteResult> AssignSystemRoleAsync(
        string userId, string roleKey, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var scope = await RoleScopeAsync(connection, transaction, roleKey, cancellationToken).ConfigureAwait(false);
        if (scope is null)
        {
            return AuthzWriteResult.Invalid($"Unknown role '{roleKey}'.");
        }

        if (!string.Equals(scope, AuthzRoles.ScopeSystem, StringComparison.Ordinal))
        {
            return AuthzWriteResult.Invalid($"Role '{roleKey}' is not a system role.");
        }

        if (!await ExistsAsync(connection, transaction, "users", "id", userId, cancellationToken).ConfigureAwait(false))
        {
            return AuthzWriteResult.Invalid($"User '{userId}' does not exist.");
        }

        try
        {
            var now = DateTime.UtcNow;
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO authz_system_role_assignments (user_id, role_key, created_at, updated_at) "
                + "VALUES (@UserId, @RoleKey, @Now, @Now);",
                new { UserId = userId, RoleKey = roleKey, Now = now }, transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return AuthzWriteResult.Ok;
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            return AuthzWriteResult.Conflict("The user already holds that system role.");
        }
    }

    public async Task<AuthzWriteResult> RevokeSystemRoleAsync(
        string userId, string roleKey, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Lock every super-admin's assignment/user/credential rows so a concurrent revoke or disable
        // serialises behind this one; the usable-survivor count is then race-free.
        var superAdmins = (await connection.QueryAsync<UsableRow>(new CommandDefinition(
            "SELECT a.user_id AS UserId, u.enabled AS Enabled, c.user_id AS CredentialUserId "
            + "FROM authz_system_role_assignments a "
            + "JOIN users u ON u.id = a.user_id "
            + "LEFT JOIN user_password_credentials c ON c.user_id = a.user_id "
            + "WHERE a.role_key = @RoleKey FOR UPDATE;",
            new { RoleKey = AuthzRoles.SuperAdmin }, transaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false)).ToList();

        var target = superAdmins.FirstOrDefault(r => string.Equals(r.UserId, userId, StringComparison.Ordinal));

        var grantExists = string.Equals(roleKey, AuthzRoles.SuperAdmin, StringComparison.Ordinal)
            ? target is not null
            : await ExistsSystemAssignmentAsync(connection, transaction, userId, roleKey, cancellationToken).ConfigureAwait(false);
        if (!grantExists)
        {
            return AuthzWriteResult.NotFound("The user does not hold that system role.");
        }

        if (string.Equals(roleKey, AuthzRoles.SuperAdmin, StringComparison.Ordinal))
        {
            var targetUsable = target is { Enabled: true, HasCredential: true };
            var otherUsable = superAdmins.Any(r =>
                !string.Equals(r.UserId, userId, StringComparison.Ordinal) && r is { Enabled: true, HasCredential: true });
            if (targetUsable && !otherUsable)
            {
                return AuthzWriteResult.Conflict("Cannot revoke the last usable super-admin.");
            }
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM authz_system_role_assignments WHERE user_id = @UserId AND role_key = @RoleKey;",
            new { UserId = userId, RoleKey = roleKey }, transaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return AuthzWriteResult.Ok;
    }

    public async Task<AuthzWriteResult> AssignOrganisationRoleAsync(
        string userId, string roleKey, string organisationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var scope = await RoleScopeAsync(connection, transaction, roleKey, cancellationToken).ConfigureAwait(false);
        if (scope is null)
        {
            return AuthzWriteResult.Invalid($"Unknown role '{roleKey}'.");
        }

        if (!string.Equals(scope, AuthzRoles.ScopeOrganisation, StringComparison.Ordinal))
        {
            return AuthzWriteResult.Invalid($"Role '{roleKey}' is not an organisation role.");
        }

        if (!await ExistsAsync(connection, transaction, "users", "id", userId, cancellationToken).ConfigureAwait(false))
        {
            return AuthzWriteResult.Invalid($"User '{userId}' does not exist.");
        }

        // Org rows live in the unified assets table; an org-role can only be assigned on a Company or
        // Department asset, never a Vendor or Machine sharing the id space.
        if (!await OrganisationExistsAsync(connection, transaction, organisationId, cancellationToken).ConfigureAwait(false))
        {
            return AuthzWriteResult.Invalid($"Organisation '{organisationId}' does not exist.");
        }

        try
        {
            var now = DateTime.UtcNow;
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO authz_organisation_role_assignments (user_id, role_key, organisation_id, created_at, updated_at) "
                + "VALUES (@UserId, @RoleKey, @OrgId, @Now, @Now);",
                new { UserId = userId, RoleKey = roleKey, OrgId = organisationId, Now = now }, transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return AuthzWriteResult.Ok;
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            return AuthzWriteResult.Conflict("The user already holds that role on the organisation.");
        }
    }

    public async Task<AuthzWriteResult> RevokeOrganisationRoleAsync(
        string userId, string roleKey, string organisationId, string actingUserId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (string.Equals(roleKey, AuthzRoles.OrgOwner, StringComparison.Ordinal))
        {
            // Lock all direct org-owner rows on this organisation, so a concurrent revoke of another
            // owner cannot leave the org ownerless by racing this one.
            var owners = (await connection.QueryAsync<string>(new CommandDefinition(
                "SELECT user_id FROM authz_organisation_role_assignments "
                + "WHERE role_key = @RoleKey AND organisation_id = @OrgId FOR UPDATE;",
                new { RoleKey = AuthzRoles.OrgOwner, OrgId = organisationId }, transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

            if (!owners.Contains(userId, StringComparer.Ordinal))
            {
                return AuthzWriteResult.NotFound("The user does not hold that role on the organisation.");
            }

            if (owners.Count == 1)
            {
                return AuthzWriteResult.Conflict("Cannot revoke the last direct org-owner of the organisation.");
            }

            // Self-lockout: a caller cannot revoke its own org-owner grant on the org it is managing,
            // which would strip its own assignment-write ability there. Checked under the same lock.
            if (string.Equals(userId, actingUserId, StringComparison.Ordinal))
            {
                return AuthzWriteResult.Conflict("Cannot revoke your own org-owner grant on this organisation.");
            }
        }
        else
        {
            var affected = await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM authz_organisation_role_assignments "
                + "WHERE user_id = @UserId AND role_key = @RoleKey AND organisation_id = @OrgId;",
                new { UserId = userId, RoleKey = roleKey, OrgId = organisationId }, transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return affected > 0
                ? AuthzWriteResult.Ok
                : AuthzWriteResult.NotFound("The user does not hold that role on the organisation.");
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM authz_organisation_role_assignments "
            + "WHERE user_id = @UserId AND role_key = @RoleKey AND organisation_id = @OrgId;",
            new { UserId = userId, RoleKey = roleKey, OrgId = organisationId }, transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return AuthzWriteResult.Ok;
    }

    public async Task<AuthzWriteResult> CreateCustomRoleAsync(
        string roleKey, string title, string description, IReadOnlyCollection<string> permissionKeys,
        string actorUserId, CancellationToken cancellationToken = default)
    {
        permissionKeys ??= [];
        description ??= string.Empty;
        if (!AuthzCustomRoles.IsAuthorableRoleKey(roleKey))
        {
            return AuthzWriteResult.Invalid($"Role key '{roleKey}' is not an authorable custom-role key.");
        }

        if (ValidateRoleFields(title, description, permissionKeys) is { } invalid)
        {
            return invalid;
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var now = DateTime.UtcNow;
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO authz_roles (role_key, title, description, scope, is_system, created_at, updated_at) "
                + "VALUES (@RoleKey, @Title, @Description, @Scope, 0, @Now, @Now);",
                new { RoleKey = roleKey, Title = title, Description = description, Scope = AuthzRoles.ScopeOrganisation, Now = now },
                transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

            await InsertPermissionsAsync(connection, transaction, roleKey, permissionKeys, cancellationToken).ConfigureAwait(false);
            await InsertAuditEventAsync(connection, transaction, RoleAudit(RoleCreateEvent, actorUserId, roleKey), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return AuthzWriteResult.Ok;
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            return AuthzWriteResult.Conflict($"A role named '{roleKey}' already exists.");
        }
    }

    public async Task<AuthzWriteResult> UpdateCustomRoleAsync(
        string roleKey, string title, string description, IReadOnlyCollection<string> permissionKeys,
        string actorUserId, CancellationToken cancellationToken = default)
    {
        permissionKeys ??= [];
        description ??= string.Empty;
        if (ValidateRoleFields(title, description, permissionKeys) is { } invalid)
        {
            return invalid;
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var isSystem = await LockRoleAsync(connection, transaction, roleKey, cancellationToken).ConfigureAwait(false);
        if (isSystem is null)
        {
            return AuthzWriteResult.NotFound($"Unknown role '{roleKey}'.");
        }

        if (isSystem.Value)
        {
            return AuthzWriteResult.Invalid($"Role '{roleKey}' is a seeded role and cannot be edited.");
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE authz_roles SET title = @Title, description = @Description, updated_at = @Now WHERE role_key = @RoleKey;",
            new { RoleKey = roleKey, Title = title, Description = description, Now = DateTime.UtcNow },
            transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM authz_role_permissions WHERE role_key = @RoleKey;",
            new { RoleKey = roleKey }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        await InsertPermissionsAsync(connection, transaction, roleKey, permissionKeys, cancellationToken).ConfigureAwait(false);
        await InsertAuditEventAsync(connection, transaction, RoleAudit(RoleUpdateEvent, actorUserId, roleKey), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return AuthzWriteResult.Ok;
    }

    public async Task<AuthzWriteResult> DeleteCustomRoleAsync(
        string roleKey, string actorUserId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var isSystem = await LockRoleAsync(connection, transaction, roleKey, cancellationToken).ConfigureAwait(false);
        if (isSystem is null)
        {
            return AuthzWriteResult.NotFound($"Unknown role '{roleKey}'.");
        }

        if (isSystem.Value)
        {
            return AuthzWriteResult.Invalid($"Role '{roleKey}' is a seeded role and cannot be deleted.");
        }

        var assignments = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT "
            + "(SELECT COUNT(*) FROM authz_organisation_role_assignments WHERE role_key = @RoleKey) "
            + "+ (SELECT COUNT(*) FROM authz_system_role_assignments WHERE role_key = @RoleKey);",
            new { RoleKey = roleKey }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (assignments > 0)
        {
            return AuthzWriteResult.Conflict($"Role '{roleKey}' has live assignments and cannot be deleted.");
        }

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM authz_roles WHERE role_key = @RoleKey;",
                new { RoleKey = roleKey }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            await InsertAuditEventAsync(connection, transaction, RoleAudit(RoleDeleteEvent, actorUserId, roleKey), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return AuthzWriteResult.Ok;
        }
        catch (MySqlException ex) when (ex.ErrorCode is MySqlErrorCode.RowIsReferenced or MySqlErrorCode.RowIsReferenced2)
        {
            // An assignment inserted after the count above trips the ON DELETE RESTRICT FK; treat it as
            // the same in-use conflict rather than surfacing a raw FK error.
            return AuthzWriteResult.Conflict($"Role '{roleKey}' has live assignments and cannot be deleted.");
        }
    }

    public async Task AppendAuditEventAsync(AuthzAuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await InsertAuditEventAsync(connection, null, auditEvent, cancellationToken).ConfigureAwait(false);
    }

    private const string RoleCreateEvent = "authz.role.create";
    private const string RoleUpdateEvent = "authz.role.update";
    private const string RoleDeleteEvent = "authz.role.delete";
    private const string RoleResourceType = "authz_role";

    private static AuthzAuditEvent RoleAudit(string eventType, string actorUserId, string roleKey)
        => new(eventType, actorUserId, AuthzActions.SystemAdmin, RoleResourceType, roleKey, null, "Permit", null);

    private static AuthzWriteResult? ValidateRoleFields(
        string title, string description, IReadOnlyCollection<string> permissionKeys)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 190)
        {
            return AuthzWriteResult.Invalid("Title must be non-blank and at most 190 characters.");
        }

        // Description is optional: callers coerce a null/omitted value to an empty string before this
        // runs, so only the column width is enforced here.
        if (description is { Length: > 512 })
        {
            return AuthzWriteResult.Invalid("Description must be at most 512 characters.");
        }

        if (permissionKeys.Any(k => !AuthzCustomRoles.AuthorablePermissionKeys.Contains(k)))
        {
            return AuthzWriteResult.Invalid("A submitted permission key is not authorable.");
        }

        return null;
    }

    // Locks the role row (FOR UPDATE) so a concurrent update/delete serialises; returns the row's
    // is_system flag, or null when the role does not exist.
    private static async Task<bool?> LockRoleAsync(
        DbConnection connection, DbTransaction transaction, string roleKey, CancellationToken cancellationToken)
        => await connection.ExecuteScalarAsync<bool?>(new CommandDefinition(
            "SELECT is_system FROM authz_roles WHERE role_key = @RoleKey FOR UPDATE;",
            new { RoleKey = roleKey }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

    private static async Task InsertPermissionsAsync(
        DbConnection connection, DbTransaction transaction, string roleKey,
        IReadOnlyCollection<string> permissionKeys, CancellationToken cancellationToken)
    {
        // Collapse exact duplicates: a role either has a permission or not, and the (role_key,
        // permission_key) primary key would otherwise reject a repeated submitted key mid-transaction.
        var distinct = permissionKeys.Distinct(StringComparer.Ordinal).ToArray();
        if (distinct.Length == 0)
        {
            return;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO authz_role_permissions (role_key, permission_key) VALUES (@RoleKey, @PermissionKey);",
            distinct.Select(k => new { RoleKey = roleKey, PermissionKey = k }),
            transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private async Task InsertAuditEventAsync(
        DbConnection connection, DbTransaction? transaction, AuthzAuditEvent auditEvent, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO authz_audit_events "
            + "(id, occurred_at, event_type, actor_user_id, action, resource_type, resource_id, organisation_id, effect, reason) "
            + "VALUES (@Id, @Now, @EventType, @ActorUserId, @Action, @ResourceType, @ResourceId, @OrganisationId, @Effect, @Reason);",
            new
            {
                Id = ulidFactory.NewId(),
                Now = DateTime.UtcNow,
                auditEvent.EventType,
                auditEvent.ActorUserId,
                auditEvent.Action,
                auditEvent.ResourceType,
                auditEvent.ResourceId,
                auditEvent.OrganisationId,
                auditEvent.Effect,
                auditEvent.Reason,
            },
            transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static async Task<string?> RoleScopeAsync(
        DbConnection connection, DbTransaction transaction, string roleKey, CancellationToken cancellationToken)
        => await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT scope FROM authz_roles WHERE role_key = @RoleKey;",
            new { RoleKey = roleKey }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

    private static async Task<bool> ExistsAsync(
        DbConnection connection, DbTransaction transaction, string table, string column, string id,
        CancellationToken cancellationToken)
    {
        // table/column are fixed internal constants, never caller input.
        var count = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COUNT(*) FROM {table} WHERE {column} = @Id;",
            new { Id = id }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return count > 0;
    }

    private static async Task<bool> OrganisationExistsAsync(
        DbConnection connection, DbTransaction transaction, string id, CancellationToken cancellationToken)
    {
        var count = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM assets WHERE id = @Id AND type IN ('Company', 'Department');",
            new { Id = id }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return count > 0;
    }

    private static async Task<bool> ExistsSystemAssignmentAsync(
        DbConnection connection, DbTransaction transaction, string userId, string roleKey,
        CancellationToken cancellationToken)
    {
        var count = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM authz_system_role_assignments WHERE user_id = @UserId AND role_key = @RoleKey;",
            new { UserId = userId, RoleKey = roleKey }, transaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return count > 0;
    }

    // MySQL has no boolean type, so credential presence is selected as the nullable credential
    // user_id (maps cleanly to string?) rather than an IS NOT NULL expression, which returns a
    // BIGINT that Dapper cannot bind to a bool constructor parameter.
    private sealed record UsableRow(string UserId, bool Enabled, string? CredentialUserId)
    {
        public bool HasCredential => CredentialUserId is not null;
    }
}
