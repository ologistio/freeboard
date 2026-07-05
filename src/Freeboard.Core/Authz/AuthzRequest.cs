namespace Freeboard.Core.Authz;

/// <summary>
/// One org grant a principal holds, already resolved to an EFFECTIVE permission: the role's
/// permission key paired with the organisation the grant sits on. A role assignment expands to one
/// fact per permission the role carries.
/// </summary>
public sealed record AuthzOrgGrant(string PermissionKey, string OrganisationId);

/// <summary>
/// The authenticated actor. Immutable. <see cref="SystemPermissions"/> are permission keys held
/// system-wide (from system-scoped role assignments, e.g. <c>system.admin</c>);
/// <see cref="OrgGrants"/> are effective <c>(permission, organisation)</c> facts from org-scoped
/// assignments. The session flags are attributes: a limited (force-reset) session is a hard-deny
/// input.
/// </summary>
public sealed record AuthzPrincipal(
    string? UserId,
    bool IsAuthenticated,
    bool IsLimitedSession,
    bool IsSteppedUp,
    IReadOnlySet<string> SystemPermissions,
    IReadOnlyCollection<AuthzOrgGrant> OrgGrants);

/// <summary>
/// The resource an action targets. <see cref="OrgAncestryInclusive"/> is the resource org's full
/// inclusive ancestry <c>[R, parent(R), ..., root]</c> built by the shared parent-walk; RBAC permits
/// when a grant sits on ANY org in this chain. For a non-org resource it is empty.
/// </summary>
public sealed record AuthzResource(
    string Type,
    string? Id,
    string? OrganisationId,
    IReadOnlyList<string> OrgAncestryInclusive)
{
    public static AuthzResource ForUser(string? userId) => new("user", userId, null, []);
}

/// <summary>An immutable authorization question: who, what, on which resource.</summary>
public sealed record AuthzRequest(AuthzPrincipal Principal, string Action, AuthzResource Resource);
