using System.Collections.Concurrent;
using Freeboard.Core.Authz;
using Freeboard.Persistence;

namespace Freeboard.Web.Tests;

/// <summary>
/// In-memory <see cref="IAuthzStore"/> for web tests. Facts are seeded as effective permission keys
/// (system) and effective <c>(permission, org)</c> grants, mirroring what the real store returns after
/// joining roles to permissions. When <see cref="Unreachable"/> is true the fact load throws so a test
/// can prove the authorizer fails closed.
/// </summary>
internal sealed class FakeAuthzStore : IAuthzStore
{
    private readonly Dictionary<string, HashSet<string>> _system = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<AuthzOrgGrant>> _org = new(StringComparer.Ordinal);

    public bool Unreachable { get; init; }

    /// <summary>
    /// Custom-role rows, shared with the admin fake (wired by the factory) so a write is visible to a
    /// later read, mirroring the two real stores over one database.
    /// </summary>
    public Dictionary<string, RoleWithPermissions> Roles { get; } = new(StringComparer.Ordinal);

    public FakeAuthzStore GrantSuperAdmin(string userId)
    {
        System(userId).Add(AuthzActions.SystemAdmin);
        return this;
    }

    public FakeAuthzStore GrantSystem(string userId, string permission)
    {
        System(userId).Add(permission);
        return this;
    }

    public FakeAuthzStore GrantOrg(string userId, string permission, string organisationId)
    {
        Org(userId).Add(new AuthzOrgGrant(permission, organisationId));
        return this;
    }

    /// <summary>Grants the effective read+write compliance permissions of an org-owner on an org.</summary>
    public FakeAuthzStore GrantOrgOwner(string userId, string organisationId)
    {
        foreach (var p in new[]
                 {
                     AuthzActions.OrgRead, AuthzActions.OrgWrite, AuthzActions.ComplianceRead,
                     AuthzActions.ComplianceScopeWrite, AuthzActions.ComplianceRequirementScopeWrite,
                     AuthzActions.AuthzAssignmentWrite,
                 })
        {
            Org(userId).Add(new AuthzOrgGrant(p, organisationId));
        }

        return this;
    }

    /// <summary>Grants the effective read-only permissions of a compliance-reader on an org.</summary>
    public FakeAuthzStore GrantComplianceReader(string userId, string organisationId)
    {
        Org(userId).Add(new AuthzOrgGrant(AuthzActions.OrgRead, organisationId));
        Org(userId).Add(new AuthzOrgGrant(AuthzActions.ComplianceRead, organisationId));
        return this;
    }

    public Task<AuthzPrincipalFacts> LoadPrincipalFactsAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (Unreachable)
        {
            throw new InvalidOperationException("authz store unreachable");
        }

        var sys = _system.TryGetValue(userId, out var s) ? new HashSet<string>(s, StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
        var grants = _org.TryGetValue(userId, out var g) ? g.ToList() : [];
        return Task.FromResult(new AuthzPrincipalFacts(sys, grants));
    }

    public Task<IReadOnlyList<SystemRoleAssignmentRow>> ListSystemAssignmentsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<SystemRoleAssignmentRow>>(
            _system.Where(kv => kv.Value.Contains(AuthzActions.SystemAdmin))
                .Select(kv => new SystemRoleAssignmentRow(kv.Key, AuthzRoles.SuperAdmin, DateTime.UtcNow))
                .ToList());

    public Task<IReadOnlyList<OrganisationRoleAssignmentRow>> ListOrganisationAssignmentsAsync(
        string organisationId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<OrganisationRoleAssignmentRow>>([]);

    public Task<IReadOnlyList<CustomRoleRow>> ListCustomRolesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CustomRoleRow>>(
            Roles.Values.Where(r => !r.Role.IsSystem).Select(r => r.Role).OrderBy(r => r.RoleKey, StringComparer.Ordinal).ToList());

    public Task<RoleWithPermissions?> GetRoleAsync(string roleKey, CancellationToken cancellationToken = default)
        => Task.FromResult(Roles.TryGetValue(roleKey, out var role) ? role : null);

    private HashSet<string> System(string userId)
        => _system.TryGetValue(userId, out var s) ? s : _system[userId] = new HashSet<string>(StringComparer.Ordinal);

    private List<AuthzOrgGrant> Org(string userId)
        => _org.TryGetValue(userId, out var g) ? g : _org[userId] = [];
}

/// <summary>
/// In-memory <see cref="IAuthzAdministrationStore"/> for web tests. Records audit events for
/// assertion and implements assign/revoke with the last-super-admin and last-direct-owner guards over
/// its in-memory assignment sets.
/// </summary>
internal sealed class FakeAuthzAdministrationStore : IAuthzAdministrationStore
{
    private readonly HashSet<string> _superAdmins = new(StringComparer.Ordinal);
    private readonly HashSet<(string User, string Role, string Org)> _orgAssignments = new();
    private Dictionary<string, RoleWithPermissions> _roles = new(StringComparer.Ordinal);

    public ConcurrentQueue<AuthzAuditEvent> Events { get; } = new();

    /// <summary>Shares the read fake's custom-role dictionary so writes here are visible to reads there.</summary>
    public void ShareRolesWith(FakeAuthzStore store) => _roles = store.Roles;

    /// <summary>Seeds a custom role directly, bypassing validation, so a test can set up an edit/delete case.</summary>
    public void SeedCustomRole(string roleKey, params string[] permissionKeys)
        => _roles[roleKey] = new RoleWithPermissions(
            new CustomRoleRow(roleKey, roleKey, string.Empty, AuthzRoles.ScopeOrganisation, false, DateTime.UtcNow, DateTime.UtcNow),
            permissionKeys.ToList());

    /// <summary>When true, <see cref="AppendAuditEventAsync"/> throws, so a test can prove the mutation
    /// audit is best-effort (logged, not fatal).</summary>
    public bool ThrowOnAudit { get; init; }

    public void SeedSuperAdmin(string userId) => _superAdmins.Add(userId);

    public void SeedOrgAssignment(string userId, string role, string org) => _orgAssignments.Add((userId, role, org));

    public Task<AuthzWriteResult> AssignSystemRoleAsync(string userId, string roleKey, CancellationToken cancellationToken = default)
        => Task.FromResult(_superAdmins.Add(userId) ? AuthzWriteResult.Ok : AuthzWriteResult.Conflict("already"));

    public Task<AuthzWriteResult> RevokeSystemRoleAsync(string userId, string roleKey, CancellationToken cancellationToken = default)
    {
        if (!_superAdmins.Contains(userId))
        {
            return Task.FromResult(AuthzWriteResult.NotFound("not held"));
        }

        if (_superAdmins.Count == 1)
        {
            return Task.FromResult(AuthzWriteResult.Conflict("last super-admin"));
        }

        _superAdmins.Remove(userId);
        return Task.FromResult(AuthzWriteResult.Ok);
    }

    public Task<AuthzWriteResult> AssignOrganisationRoleAsync(
        string userId, string roleKey, string organisationId, CancellationToken cancellationToken = default)
        => Task.FromResult(_orgAssignments.Add((userId, roleKey, organisationId))
            ? AuthzWriteResult.Ok
            : AuthzWriteResult.Conflict("already"));

    public Task<AuthzWriteResult> RevokeOrganisationRoleAsync(
        string userId, string roleKey, string organisationId, string actingUserId,
        CancellationToken cancellationToken = default)
    {
        var key = (userId, roleKey, organisationId);
        if (!_orgAssignments.Contains(key))
        {
            return Task.FromResult(AuthzWriteResult.NotFound("not held"));
        }

        if (string.Equals(roleKey, AuthzRoles.OrgOwner, StringComparison.Ordinal))
        {
            if (_orgAssignments.Count(a => a.Role == AuthzRoles.OrgOwner && a.Org == organisationId) == 1)
            {
                return Task.FromResult(AuthzWriteResult.Conflict("last org-owner"));
            }

            if (string.Equals(userId, actingUserId, StringComparison.Ordinal))
            {
                return Task.FromResult(AuthzWriteResult.Conflict("self-lockout"));
            }
        }

        _orgAssignments.Remove(key);
        return Task.FromResult(AuthzWriteResult.Ok);
    }

    public Task<AuthzWriteResult> CreateCustomRoleAsync(
        string roleKey, string title, string description, IReadOnlyCollection<string> permissionKeys,
        string actorUserId, CancellationToken cancellationToken = default)
    {
        if (!AuthzCustomRoles.IsAuthorableRoleKey(roleKey))
        {
            return Task.FromResult(AuthzWriteResult.Invalid("bad key"));
        }

        if (ValidateFields(title, description, permissionKeys) is { } invalid)
        {
            return Task.FromResult(invalid);
        }

        if (_roles.ContainsKey(roleKey))
        {
            return Task.FromResult(AuthzWriteResult.Conflict("duplicate"));
        }

        var now = DateTime.UtcNow;
        _roles[roleKey] = new RoleWithPermissions(
            new CustomRoleRow(roleKey, title, description, AuthzRoles.ScopeOrganisation, false, now, now),
            permissionKeys.ToList());
        Events.Enqueue(RoleAudit("authz.role.create", actorUserId, roleKey));
        return Task.FromResult(AuthzWriteResult.Ok);
    }

    public Task<AuthzWriteResult> UpdateCustomRoleAsync(
        string roleKey, string title, string description, IReadOnlyCollection<string> permissionKeys,
        string actorUserId, CancellationToken cancellationToken = default)
    {
        if (!_roles.TryGetValue(roleKey, out var existing))
        {
            return Task.FromResult(AuthzWriteResult.NotFound("unknown"));
        }

        if (existing.Role.IsSystem)
        {
            return Task.FromResult(AuthzWriteResult.Invalid("seeded"));
        }

        if (ValidateFields(title, description, permissionKeys) is { } invalid)
        {
            return Task.FromResult(invalid);
        }

        _roles[roleKey] = new RoleWithPermissions(
            existing.Role with { Title = title, Description = description, UpdatedAt = DateTime.UtcNow },
            permissionKeys.ToList());
        Events.Enqueue(RoleAudit("authz.role.update", actorUserId, roleKey));
        return Task.FromResult(AuthzWriteResult.Ok);
    }

    public Task<AuthzWriteResult> DeleteCustomRoleAsync(
        string roleKey, string actorUserId, CancellationToken cancellationToken = default)
    {
        if (!_roles.TryGetValue(roleKey, out var existing))
        {
            return Task.FromResult(AuthzWriteResult.NotFound("unknown"));
        }

        if (existing.Role.IsSystem)
        {
            return Task.FromResult(AuthzWriteResult.Invalid("seeded"));
        }

        if (_orgAssignments.Any(a => a.Role == roleKey))
        {
            return Task.FromResult(AuthzWriteResult.Conflict("in use"));
        }

        _roles.Remove(roleKey);
        Events.Enqueue(RoleAudit("authz.role.delete", actorUserId, roleKey));
        return Task.FromResult(AuthzWriteResult.Ok);
    }

    public Task AppendAuditEventAsync(AuthzAuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        if (ThrowOnAudit)
        {
            throw new InvalidOperationException("audit store unreachable");
        }

        Events.Enqueue(auditEvent);
        return Task.CompletedTask;
    }

    private static AuthzAuditEvent RoleAudit(string eventType, string actorUserId, string roleKey)
        => new(eventType, actorUserId, AuthzActions.SystemAdmin, "authz_role", roleKey, null, "Permit", null);

    private static AuthzWriteResult? ValidateFields(
        string title, string description, IReadOnlyCollection<string> permissionKeys)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 190)
        {
            return AuthzWriteResult.Invalid("title");
        }

        if (description is { Length: > 512 })
        {
            return AuthzWriteResult.Invalid("description");
        }

        return permissionKeys.Any(k => !AuthzCustomRoles.AuthorablePermissionKeys.Contains(k))
            ? AuthzWriteResult.Invalid("permission")
            : null;
    }
}
