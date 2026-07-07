using Freeboard.Persistence;

namespace Freeboard.Web.Tests;

/// <summary>
/// In-memory <see cref="IComplianceStore"/> double for web tests so the suite is green
/// without MySQL. When <see cref="Unreachable"/> is true, every read throws to
/// simulate a down store.
/// </summary>
internal sealed class FakeComplianceStore : IComplianceStore
{
    public bool Unreachable { get; init; }

    /// <summary>
    /// When true, the reads that surface the organisation list - <see cref="GetOrganisationsAsync"/>
    /// and <see cref="GetStatementOfApplicabilityInputsAsync"/> - throw; the other reads succeed.
    /// </summary>
    public bool OrganisationsUnreachable { get; init; }

    public IReadOnlyList<StandardRow> Standards { get; set; } = [];

    public IReadOnlyList<RequirementRow> Requirements { get; set; } = [];

    public IReadOnlyList<ControlRow> Controls { get; set; } = [];

    public IReadOnlyList<OrganisationRow> Organisations { get; set; } = [];

    public IReadOnlyList<ScopeRow> Scopes { get; set; } = [];

    public IReadOnlyList<RequirementScopeRow> RequirementScopes { get; set; } = [];

    public IReadOnlyList<VendorRow> Vendors { get; set; } = [];

    public IReadOnlyList<VendorScopeRow> VendorScopes { get; set; } = [];

    public IReadOnlyList<EvidenceCollectorRow> Collectors { get; set; } = [];

    public IReadOnlyList<AttestationTemplateRow> Templates { get; set; } = [];

    public Task<IReadOnlyList<StandardRow>> GetStandardsAsync(CancellationToken cancellationToken = default) =>
        Guard(() => Standards);

    public Task<IReadOnlyList<RequirementRow>> GetRequirementsAsync(CancellationToken cancellationToken = default) =>
        Guard(() => Requirements);

    public Task<IReadOnlyList<ControlRow>> GetControlsAsync(CancellationToken cancellationToken = default) =>
        Guard(() => Controls);

    public Task<IReadOnlyList<OrganisationRow>> GetOrganisationsAsync(CancellationToken cancellationToken = default)
    {
        if (OrganisationsUnreachable)
        {
            throw new InvalidOperationException("organisations unreachable");
        }

        return Guard(() => Organisations);
    }

    public Task<IReadOnlyList<ScopeRow>> GetScopesAsync(CancellationToken cancellationToken = default) =>
        Guard(() => Scopes);

    public Task<IReadOnlyList<RequirementScopeRow>> GetRequirementScopesAsync(CancellationToken cancellationToken = default) =>
        Guard(() => RequirementScopes);

    public Task<IReadOnlyList<VendorRow>> GetVendorsAsync(CancellationToken cancellationToken = default) =>
        Guard(() => Vendors);

    public Task<IReadOnlyList<VendorScopeRow>> GetVendorScopesAsync(CancellationToken cancellationToken = default) =>
        Guard(() => VendorScopes);

    public Task<IReadOnlyList<EvidenceCollectorRow>> GetEvidenceCollectorsAsync(CancellationToken cancellationToken = default) =>
        Guard(() => Collectors);

    public Task<IReadOnlyList<AttestationTemplateRow>> GetAttestationTemplatesAsync(CancellationToken cancellationToken = default) =>
        Guard(() => Templates);

    public Task<SoaInputs> GetStatementOfApplicabilityInputsAsync(CancellationToken cancellationToken = default)
    {
        if (OrganisationsUnreachable)
        {
            throw new InvalidOperationException("organisations unreachable");
        }

        return Guard(() => new SoaInputs(Organisations, Scopes, Requirements, RequirementScopes));
    }

    public Task<ComplianceCounts> GetCountsAsync(CancellationToken cancellationToken = default) =>
        Guard(() => new ComplianceCounts(
            Standards.Count, Controls.Count, Requirements.Count, Organisations.Count, Scopes.Count,
            RequirementScopes.Count, Vendors.Count, VendorScopes.Count, Collectors.Count, Templates.Count));

    private Task<T> Guard<T>(Func<T> value)
    {
        if (Unreachable)
        {
            throw new InvalidOperationException("store unreachable");
        }

        return Task.FromResult(value());
    }
}
