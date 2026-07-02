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

    public IReadOnlyList<StandardRow> Standards { get; set; } = [];

    public IReadOnlyList<RequirementRow> Requirements { get; set; } = [];

    public IReadOnlyList<ControlRow> Controls { get; set; } = [];

    public IReadOnlyList<OrganisationRow> Organisations { get; set; } = [];

    public IReadOnlyList<ScopeRow> Scopes { get; set; } = [];

    public Task<IReadOnlyList<StandardRow>> GetStandardsAsync(CancellationToken cancellationToken = default) =>
        Guard(() => Standards);

    public Task<IReadOnlyList<RequirementRow>> GetRequirementsAsync(CancellationToken cancellationToken = default) =>
        Guard(() => Requirements);

    public Task<IReadOnlyList<ControlRow>> GetControlsAsync(CancellationToken cancellationToken = default) =>
        Guard(() => Controls);

    public Task<IReadOnlyList<OrganisationRow>> GetOrganisationsAsync(CancellationToken cancellationToken = default) =>
        Guard(() => Organisations);

    public Task<IReadOnlyList<ScopeRow>> GetScopesAsync(CancellationToken cancellationToken = default) =>
        Guard(() => Scopes);

    public Task<ComplianceCounts> GetCountsAsync(CancellationToken cancellationToken = default) =>
        Guard(() => new ComplianceCounts(
            Standards.Count, Controls.Count, Requirements.Count, Organisations.Count, Scopes.Count));

    private Task<T> Guard<T>(Func<T> value)
    {
        if (Unreachable)
        {
            throw new InvalidOperationException("store unreachable");
        }

        return Task.FromResult(value());
    }
}
