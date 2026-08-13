namespace Freeboard.Persistence;

/// <summary>
/// The general read abstraction over the persisted compliance domain. Returns
/// standards, controls (with resolved <c>maps_to</c>), the unified asset set (with its
/// <c>parent</c> and <c>owner</c> edges), and scopes (subject, target, disposition), plus per-kind
/// counts. Reads are ordered by <c>id</c> and each relation id array is ordered by id.
/// This is the only persistence surface the web app depends on.
/// </summary>
public interface IComplianceStore
{
    Task<IReadOnlyList<StandardRow>> GetStandardsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RequirementRow>> GetRequirementsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ControlRow>> GetControlsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Every asset of every type, unfiltered. Resolution, read-access, and the live-subject predicate
    /// each need a different subset, so type and retirement are applied by the caller.
    /// </summary>
    Task<IReadOnlyList<AssetNode>> GetAssetsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ScopeRow>> GetScopesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CollectorRow>> GetCollectorsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IntegrationConnectionRow>> GetIntegrationConnectionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the Statement of Applicability inputs (the assets, the unified scopes, and requirements)
    /// together in one repeatable-read snapshot so they cannot straddle a concurrent importer commit.
    /// </summary>
    Task<SoaInputs> GetStatementOfApplicabilityInputsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the Statement of Applicability drill-down inputs (the assets, the unified scopes,
    /// requirements, controls with resolved <c>maps_to</c>, and collectors) together in one
    /// repeatable-read snapshot so the drill-down hierarchy cannot straddle a concurrent importer
    /// commit. Separate from <see cref="GetStatementOfApplicabilityInputsAsync"/> so evidence ingest and
    /// the JSON endpoint keep their lighter three-list read.
    /// </summary>
    Task<SoaDrilldownInputs> GetStatementOfApplicabilityDrilldownInputsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the assets and the whole vendor assurance set together in one repeatable-read snapshot. There
    /// is no standalone assurance read: every caller narrows the assurances by the <c>owner</c> edges on
    /// the asset rows, so a lone assurance read has no honest caller. The caller groups the assurances by
    /// vendor, matching how the register reads scopes.
    /// </summary>
    Task<VendorAssuranceInputs> GetVendorAssuranceInputsAsync(CancellationToken cancellationToken = default);

    Task<ComplianceCounts> GetCountsAsync(CancellationToken cancellationToken = default);
}
