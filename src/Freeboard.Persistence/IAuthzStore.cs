using Freeboard.Core.Authz;

namespace Freeboard.Persistence;

/// <summary>
/// The authz read store. Loads a principal's effective facts in bounded queries (no per-request
/// N+1) and lists assignments for the management UI. Both fact queries join <c>authz_roles.scope</c>
/// and defensively drop a mis-scoped assignment row, so a stray row from a schema breach can never
/// contribute <c>system.admin</c> to a principal's facts.
/// </summary>
public interface IAuthzStore
{
    Task<AuthzPrincipalFacts> LoadPrincipalFactsAsync(string userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SystemRoleAssignmentRow>> ListSystemAssignmentsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OrganisationRoleAssignmentRow>> ListOrganisationAssignmentsAsync(
        string organisationId, CancellationToken cancellationToken = default);
}
