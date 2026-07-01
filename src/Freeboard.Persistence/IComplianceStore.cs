namespace Freeboard.Persistence;

/// <summary>
/// The general read abstraction over the persisted compliance domain. Returns
/// standards, controls (with resolved <c>maps_to</c>), organisations (with resolved
/// <c>parent</c>), and scopes (organisation, standard, disposition), plus per-kind
/// counts. Reads are ordered by <c>id</c> and each relation id array is ordered by id.
/// This is the only persistence surface the web app depends on.
/// </summary>
public interface IComplianceStore
{
    Task<IReadOnlyList<StandardRow>> GetStandardsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ControlRow>> GetControlsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OrganisationRow>> GetOrganisationsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ScopeRow>> GetScopesAsync(CancellationToken cancellationToken = default);

    Task<ComplianceCounts> GetCountsAsync(CancellationToken cancellationToken = default);
}
