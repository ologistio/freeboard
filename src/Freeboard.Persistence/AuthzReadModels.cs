namespace Freeboard.Persistence;

/// <summary>A system-scoped role assignment (e.g. a <c>super-admin</c> holder) for the management UI.</summary>
public sealed record SystemRoleAssignmentRow(string UserId, string RoleKey, DateTime CreatedAt);

/// <summary>An org-scoped role assignment on one organisation, for the management UI.</summary>
public sealed record OrganisationRoleAssignmentRow(
    string UserId, string RoleKey, string OrganisationId, DateTime CreatedAt);

/// <summary>One <c>authz_roles</c> row, for the custom-role management UI.</summary>
public sealed record CustomRoleRow(
    string RoleKey, string Title, string Description, string Scope, bool IsSystem,
    DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>A role plus the permission keys it grants, for the custom-role edit view.</summary>
public sealed record RoleWithPermissions(CustomRoleRow Role, IReadOnlyList<string> PermissionKeys);

/// <summary>
/// One row to persist in <c>authz_audit_events</c>. Scalar ids only (no strict FKs), so the trail
/// survives the deletion of the actor or the referenced resource. The store stamps id and occurred_at.
/// </summary>
public sealed record AuthzAuditEvent(
    string EventType,
    string? ActorUserId,
    string? Action,
    string? ResourceType,
    string? ResourceId,
    string? OrganisationId,
    string? Effect,
    string? Reason);

/// <summary>The status of an authz administration write, so the endpoint can map it to a status code.</summary>
public enum AuthzWriteStatus
{
    Ok,
    NotFound,
    Conflict,
    Invalid,
}

/// <summary>
/// The outcome of an authz administration write. Richer than <see cref="WriteResult"/> because the
/// role-assignment contracts distinguish 404 (grant absent), 409 (duplicate or a guard violation such
/// as removing the last usable super-admin), and 422 (validation, e.g. an unknown or mis-scoped role).
/// </summary>
public sealed record AuthzWriteResult(AuthzWriteStatus Status, string? Error)
{
    public static readonly AuthzWriteResult Ok = new(AuthzWriteStatus.Ok, null);

    public bool IsOk => Status == AuthzWriteStatus.Ok;

    public static AuthzWriteResult NotFound(string error) => new(AuthzWriteStatus.NotFound, error);

    public static AuthzWriteResult Conflict(string error) => new(AuthzWriteStatus.Conflict, error);

    public static AuthzWriteResult Invalid(string error) => new(AuthzWriteStatus.Invalid, error);
}
