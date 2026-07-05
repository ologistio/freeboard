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

        if (!await ExistsAsync(connection, transaction, "organisations", "id", organisationId, cancellationToken).ConfigureAwait(false))
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

    public async Task AppendAuditEventAsync(AuthzAuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
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
            cancellationToken: cancellationToken)).ConfigureAwait(false);
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
