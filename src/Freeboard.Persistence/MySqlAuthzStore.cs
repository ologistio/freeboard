using System.Data;
using Dapper;
using Freeboard.Core.Authz;

namespace Freeboard.Persistence;

/// <summary>
/// MySQL-backed <see cref="IAuthzStore"/>. Fact loading is two bounded queries (system permissions,
/// org grants) read in one <c>RepeatableRead</c> snapshot so a single decision's facts cannot straddle
/// a concurrent assignment change. Each fact query joins <c>authz_roles.scope</c> so a mis-scoped
/// assignment row (a <c>system</c> role in the org table or an <c>organisation</c> role in the system
/// table) contributes nothing.
/// </summary>
public sealed class MySqlAuthzStore(IDbConnectionFactory connectionFactory) : IAuthzStore
{
    public async Task<AuthzPrincipalFacts> LoadPrincipalFactsAsync(
        string userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);

        var systemPermissions = (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT DISTINCT rp.permission_key "
            + "FROM authz_system_role_assignments a "
            + "JOIN authz_roles r ON r.role_key = a.role_key AND r.scope = 'system' "
            + "JOIN authz_role_permissions rp ON rp.role_key = a.role_key "
            + "WHERE a.user_id = @UserId;",
            new { UserId = userId }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);

        var grants = (await connection.QueryAsync<(string PermissionKey, string OrganisationId)>(new CommandDefinition(
            "SELECT DISTINCT rp.permission_key AS PermissionKey, a.organisation_id AS OrganisationId "
            + "FROM authz_organisation_role_assignments a "
            + "JOIN authz_roles r ON r.role_key = a.role_key AND r.scope = 'organisation' "
            + "JOIN authz_role_permissions rp ON rp.role_key = a.role_key "
            + "WHERE a.user_id = @UserId;",
            new { UserId = userId }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false))
            .Select(g => new AuthzOrgGrant(g.PermissionKey, g.OrganisationId))
            .ToList();

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new AuthzPrincipalFacts(systemPermissions, grants);
    }

    public async Task<IReadOnlyList<SystemRoleAssignmentRow>> ListSystemAssignmentsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<SystemRoleAssignmentRow>(new CommandDefinition(
            "SELECT user_id AS UserId, role_key AS RoleKey, created_at AS CreatedAt "
            + "FROM authz_system_role_assignments ORDER BY user_id, role_key;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task<IReadOnlyList<OrganisationRoleAssignmentRow>> ListOrganisationAssignmentsAsync(
        string organisationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<OrganisationRoleAssignmentRow>(new CommandDefinition(
            "SELECT user_id AS UserId, role_key AS RoleKey, organisation_id AS OrganisationId, created_at AS CreatedAt "
            + "FROM authz_organisation_role_assignments WHERE organisation_id = @OrgId ORDER BY user_id, role_key;",
            new { OrgId = organisationId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task<IReadOnlyList<CustomRoleRow>> ListCustomRolesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<CustomRoleRow>(new CommandDefinition(
            "SELECT role_key AS RoleKey, title AS Title, description AS Description, scope AS Scope, "
            + "is_system AS IsSystem, created_at AS CreatedAt, updated_at AS UpdatedAt "
            + "FROM authz_roles WHERE is_system = 0 ORDER BY role_key;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task<RoleWithPermissions?> GetRoleAsync(string roleKey, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var role = await connection.QuerySingleOrDefaultAsync<CustomRoleRow>(new CommandDefinition(
            "SELECT role_key AS RoleKey, title AS Title, description AS Description, scope AS Scope, "
            + "is_system AS IsSystem, created_at AS CreatedAt, updated_at AS UpdatedAt "
            + "FROM authz_roles WHERE role_key = @RoleKey;",
            new { RoleKey = roleKey }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (role is null)
        {
            return null;
        }

        var permissions = (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT permission_key FROM authz_role_permissions WHERE role_key = @RoleKey ORDER BY permission_key;",
            new { RoleKey = roleKey }, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
        return new RoleWithPermissions(role, permissions);
    }
}
