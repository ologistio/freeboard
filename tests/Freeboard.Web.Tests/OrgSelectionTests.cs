using System.Security.Claims;
using Freeboard.Persistence;
using Microsoft.AspNetCore.Http;

namespace Freeboard.Web.Tests;

/// <summary>
/// Unit tests for the pure <see cref="OrgSelection.Resolve"/> rule and the request-scoped
/// <see cref="OrgSelectionResolver"/>: fail-closed resolution against the accessible set, memoized
/// reads, accessible-set bounding, and silent degrade to "All Organisations" on a store failure.
/// </summary>
public sealed class OrgSelectionTests
{
    private static IReadOnlyList<OrganisationRow> Orgs() =>
    [
        new OrganisationRow("org-a", "Org A", "Company", null),
        new OrganisationRow("org-b", "Org B", "Company", null),
    ];

    private static IReadOnlySet<string> Set(params string[] ids) =>
        new HashSet<string>(ids, StringComparer.Ordinal);

    [Fact]
    public void Resolve_NullCandidate_IsAll()
        => Assert.Null(OrgSelection.Resolve(null, Set("org-a")));

    [Fact]
    public void Resolve_AccessibleCandidate_ResolvesToItself()
        => Assert.Equal("org-a", OrgSelection.Resolve("org-a", Set("org-a", "org-b")));

    [Fact]
    public void Resolve_InaccessibleCandidate_DropsToAll()
        => Assert.Null(OrgSelection.Resolve("org-x", Set("org-a", "org-b")));

    [Fact]
    public async Task Resolver_AbsentCookie_ResolvesToAll()
    {
        var resolver = Resolver(new FakeComplianceStore { Organisations = Orgs() }, new AllOrgAccess(), cookie: null);
        var state = await resolver.GetAsync();
        Assert.Null(state.SelectedId);
    }

    [Fact]
    public async Task Resolver_AccessibleId_ResolvesToItself()
    {
        var resolver = Resolver(new FakeComplianceStore { Organisations = Orgs() }, new AllOrgAccess(), cookie: "org-a");
        var state = await resolver.GetAsync();
        Assert.Equal("org-a", state.SelectedId);
    }

    [Fact]
    public async Task Resolver_UnknownId_DropsToAll()
    {
        var resolver = Resolver(new FakeComplianceStore { Organisations = Orgs() }, new AllOrgAccess(), cookie: "org-x");
        var state = await resolver.GetAsync();
        Assert.Null(state.SelectedId);
    }

    [Fact]
    public async Task Resolver_AccessibleSetBoundsResult()
    {
        // org-b is accessible; org-a is not. The accessible set is what bounds selection and the tree.
        var resolver = Resolver(
            new FakeComplianceStore { Organisations = Orgs() }, new RestrictedOrgAccess(Set("org-b")), cookie: "org-a");
        var state = await resolver.GetAsync();
        Assert.Null(state.SelectedId);
        Assert.Equal(Set("org-b"), state.AccessibleIds);
    }

    [Fact]
    public async Task Resolver_RepeatedReads_HitStoreOnce()
    {
        var store = new CountingComplianceStore { Organisations = Orgs() };
        var resolver = Resolver(store, new AllOrgAccess(), cookie: "org-a");
        await resolver.GetAsync();
        await resolver.GetAsync();
        Assert.Equal(1, store.OrganisationReads);
    }

    [Fact]
    public async Task Resolver_StoreFailure_DegradesToAllWithEmptyList()
    {
        var resolver = Resolver(new FakeComplianceStore { Unreachable = true }, new AllOrgAccess(), cookie: "org-a");
        var state = await resolver.GetAsync();
        Assert.Null(state.SelectedId);
        Assert.Empty(state.Organisations);
        Assert.Empty(state.AccessibleIds);
    }

    private static OrgSelectionResolver Resolver(IComplianceStore store, IOrgAccess access, string? cookie)
    {
        var context = new DefaultHttpContext();
        if (cookie is not null)
        {
            context.Request.Headers.Cookie = $"{OrgSelection.CookieName}={cookie}";
        }

        return new OrgSelectionResolver(new HttpContextAccessor { HttpContext = context }, store, access);
    }

    private sealed class RestrictedOrgAccess(IReadOnlySet<string> accessible) : IOrgAccess
    {
        public IReadOnlySet<string> AccessibleOrgIds(
            ClaimsPrincipal user, IReadOnlyList<OrganisationRow> organisations)
            => accessible;
    }

    private sealed class CountingComplianceStore : IComplianceStore
    {
        public int OrganisationReads { get; private set; }

        public IReadOnlyList<OrganisationRow> Organisations { get; init; } = [];

        public Task<IReadOnlyList<OrganisationRow>> GetOrganisationsAsync(CancellationToken cancellationToken = default)
        {
            OrganisationReads++;
            return Task.FromResult(Organisations);
        }

        public Task<IReadOnlyList<StandardRow>> GetStandardsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult((IReadOnlyList<StandardRow>)[]);

        public Task<IReadOnlyList<RequirementRow>> GetRequirementsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult((IReadOnlyList<RequirementRow>)[]);

        public Task<IReadOnlyList<ControlRow>> GetControlsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult((IReadOnlyList<ControlRow>)[]);

        public Task<IReadOnlyList<ScopeRow>> GetScopesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult((IReadOnlyList<ScopeRow>)[]);

        public Task<IReadOnlyList<RequirementScopeRow>> GetRequirementScopesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult((IReadOnlyList<RequirementScopeRow>)[]);

        public Task<SoaInputs> GetStatementOfApplicabilityInputsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SoaInputs(Organisations, [], [], []));

        public Task<ComplianceCounts> GetCountsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ComplianceCounts(0, 0, 0, 0, 0, 0));
    }
}
