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

    Task AppendAuditEventAsync(AuthzAuditEvent auditEvent, CancellationToken cancellationToken = default);
}
