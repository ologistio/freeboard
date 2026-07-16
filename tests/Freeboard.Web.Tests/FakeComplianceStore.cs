using Freeboard.Persistence;

namespace Freeboard.Web.Tests;

/// <summary>
/// In-memory <see cref="IComplianceStore"/> double for web tests so the suite is green
/// without MySQL. When <see cref="Unreachable"/> is true, every read throws to
/// simulate a down store. <see cref="Scopes"/> carries the unified enriched
/// <see cref="ScopeRow"/>s (subject-narrowing fields set by the readability tests).
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

    public IReadOnlyList<VendorRow> Vendors { get; set; } = [];

    public IReadOnlyList<EvidenceCollectorRow> Collectors { get; set; } = [];

    public IReadOnlyList<AttestationTemplateRow> Templates { get; set; } = [];

    public IReadOnlyList<IntegrationConnectionRow> Connections { get; set; } = [];

    /// <summary>
    /// The subject-resolving asset id set (present, and not a retired discovered asset) fed to the SoA inputs for the
    /// dangling-subject notice. Null defaults to the ids of every organisation and vendor in the store,
    /// so a scope whose subject is one of those resolves and any other subject dangles.
    /// </summary>
    public IReadOnlySet<string>? ResolvableAssetIds { get; set; }

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
        Guard(() => (IReadOnlyList<ScopeRow>)Scopes.Select(ResolveSubject).ToList());

    // Mirror the real store's LEFT JOIN assets: when a fixture leaves the subject-narrowing fields unset,
    // fill an org subject's type and parent from the Organisations list so a Company/Department subject
    // scope resolves as one. A row that sets SubjectType explicitly (a vendor or machine subject) is left
    // as authored, and a subject with no matching organisation stays unresolved (SubjectType null).
    private ScopeRow ResolveSubject(ScopeRow scope)
    {
        if (scope.SubjectType is not null)
        {
            return scope;
        }

        var org = Organisations.FirstOrDefault(o => string.Equals(o.Id, scope.Subject, StringComparison.Ordinal));
        return org is null ? scope : scope with { SubjectType = org.Kind, SubjectParent = org.Parent };
    }

    public Task<IReadOnlyList<VendorRow>> GetVendorsAsync(CancellationToken cancellationToken = default) =>
        Guard(() => Vendors);

    public Task<IReadOnlyList<EvidenceCollectorRow>> GetEvidenceCollectorsAsync(CancellationToken cancellationToken = default) =>
        Guard(() => Collectors);

    public Task<IReadOnlyList<AttestationTemplateRow>> GetAttestationTemplatesAsync(CancellationToken cancellationToken = default) =>
        Guard(() => Templates);

    public Task<IReadOnlyList<IntegrationConnectionRow>> GetIntegrationConnectionsAsync(CancellationToken cancellationToken = default) =>
        Guard(() => Connections);

    public Task<SoaInputs> GetStatementOfApplicabilityInputsAsync(CancellationToken cancellationToken = default)
    {
        if (OrganisationsUnreachable)
        {
            throw new InvalidOperationException("organisations unreachable");
        }

        return Guard(() => new SoaInputs(Organisations, Scopes, Requirements, ResolvableAssets()));
    }

    public Task<SoaDrilldownInputs> GetStatementOfApplicabilityDrilldownInputsAsync(CancellationToken cancellationToken = default)
    {
        if (OrganisationsUnreachable)
        {
            throw new InvalidOperationException("organisations unreachable");
        }

        return Guard(() => new SoaDrilldownInputs(
            Organisations, Scopes, Requirements, ResolvableAssets(), Controls, Collectors, Templates, Vendors));
    }

    public Task<ComplianceCounts> GetCountsAsync(CancellationToken cancellationToken = default) =>
        Guard(() => new ComplianceCounts(
            Standards.Count, Controls.Count, Requirements.Count, Organisations.Count, Scopes.Count,
            Vendors.Count, Collectors.Count, Templates.Count));

    private IReadOnlySet<string> ResolvableAssets() =>
        ResolvableAssetIds ?? Organisations.Select(o => o.Id)
            .Concat(Vendors.Select(v => v.Id))
            .ToHashSet(StringComparer.Ordinal);

    private Task<T> Guard<T>(Func<T> value)
    {
        if (Unreachable)
        {
            throw new InvalidOperationException("store unreachable");
        }

        return Task.FromResult(value());
    }
}
