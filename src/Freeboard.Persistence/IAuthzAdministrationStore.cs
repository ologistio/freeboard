namespace Freeboard.Persistence;

/// <summary>
/// The authz write store: grant/revoke role assignments and append audit rows. Assign validates the
/// role is known, the user/org exist, AND the role's scope matches the assignment table (a mis-scoped
/// grant returns <see cref="AuthzWriteStatus.Invalid"/> and writes nothing). The last-super-admin,
/// last-owner, and org-owner self-lockout guards are enforced INSIDE the single locking transaction
/// that performs the revoke, never as a separate check-then-write, so two concurrent revokes cannot
/// both pass.
/// </summary>
public interface IAuthzAdministrationStore
{
    Task<AuthzWriteResult> AssignSystemRoleAsync(
        string userId, string roleKey, CancellationToken cancellationToken = default);

    Task<AuthzWriteResult> RevokeSystemRoleAsync(
        string userId, string roleKey, CancellationToken cancellationToken = default);

    Task<AuthzWriteResult> AssignOrganisationRoleAsync(
        string userId, string roleKey, string organisationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes an org-scoped grant. <paramref name="actingUserId"/> is the caller performing the revoke;
    /// inside the locking transaction the store rejects removing the last direct <c>org-owner</c> of the
    /// organisation AND rejects the caller revoking its OWN <c>org-owner</c> grant (self-lockout), so a
    /// manager cannot strip its own ability to manage the org.
    /// </summary>
    Task<AuthzWriteResult> RevokeOrganisationRoleAsync(
        string userId, string roleKey, string organisationId, string actingUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an author-defined role. The store fixes <c>scope = organisation</c> and <c>is_system = 0</c>
    /// (never caller-settable), requires an authorable <paramref name="roleKey"/>, a non-blank title
    /// (&lt;= 190) and non-null description (&lt;= 512), and rejects any permission key outside the Core
    /// allow-list. The role, its permission rows, and the audit row are written in one transaction.
    /// </summary>
    Task<AuthzWriteResult> CreateCustomRoleAsync(
        string roleKey, string title, string description, IReadOnlyCollection<string> permissionKeys,
        string actorUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces a custom role's title, description, and permission set (never its <c>role_key</c>,
    /// <c>scope</c>, or <c>is_system</c>). Rejects an unknown role (404) or a seeded (<c>is_system = 1</c>)
    /// target (Invalid). The mutation and its audit row commit together.
    /// </summary>
    Task<AuthzWriteResult> UpdateCustomRoleAsync(
        string roleKey, string title, string description, IReadOnlyCollection<string> permissionKeys,
        string actorUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a custom role. Rejects a seeded target (Invalid) and a role with live assignments
    /// (Conflict, race-safe: the row is locked and assignments counted inside the transaction).
    /// An unused delete cascades the role's permission rows. The audit row commits with the delete.
    /// </summary>
    Task<AuthzWriteResult> DeleteCustomRoleAsync(
        string roleKey, string actorUserId, CancellationToken cancellationToken = default);

    Task AppendAuditEventAsync(AuthzAuditEvent auditEvent, CancellationToken cancellationToken = default);
}
