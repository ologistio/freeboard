using System.Security.Claims;
using Freeboard.Authz;
using Freeboard.Persistence;
using Microsoft.AspNetCore.Http;

namespace Freeboard.Web.Tests;

/// <summary>
/// Unit tests for the pure <see cref="OrgSelection.Resolve"/> rule and the request-scoped
/// <see cref="OrgSelectionResolver"/>: fail-closed resolution against the accessible set, memoized
/// reads, accessible-set bounding, organisation-only selection, and silent degrade to
/// "All Organisations" on a store failure.
/// </summary>
public sealed class OrgSelectionTests
{
    private static IReadOnlyList<AssetNode> Assets() =>
    [
        TestAssets.Org("org-a", title: "Org A"),
        TestAssets.Org("org-b", title: "Org B"),
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
        var resolver = Resolver(new FakeComplianceStore { Assets = Assets() }, new AllAssetAccess(), cookie: null);
        var state = await resolver.GetAsync();
        Assert.Null(state.SelectedId);
    }

    [Fact]
    public async Task Resolver_AccessibleId_ResolvesToItself()
    {
        var resolver = Resolver(new FakeComplianceStore { Assets = Assets() }, new AllAssetAccess(), cookie: "org-a");
        var state = await resolver.GetAsync();
        Assert.Equal("org-a", state.SelectedId);
    }

    [Fact]
    public async Task Resolver_UnknownId_DropsToAll()
    {
        var resolver = Resolver(new FakeComplianceStore { Assets = Assets() }, new AllAssetAccess(), cookie: "org-x");
        var state = await resolver.GetAsync();
        Assert.Null(state.SelectedId);
    }

    [Theory]
    [InlineData("m-1")]
    [InlineData("v-1")]
    public async Task Resolver_NonOrganisationCookie_DropsToAllAndIsNotAnAccessibleSelection(string cookie)
    {
        // The seam hands back an ASSET set, so a readable machine or vendor reaches the resolver. The
        // selector presents organisations only, so neither may become a selection.
        var store = new FakeComplianceStore
        {
            Assets = [.. Assets(), TestAssets.Machine("m-1", "org-a"), TestAssets.Vendor("v-1", "org-a")],
        };
        var resolver = Resolver(store, new AllAssetAccess(), cookie);

        var state = await resolver.GetAsync();

        Assert.Null(state.SelectedId);
        Assert.Equal(Set("org-a", "org-b"), state.AccessibleIds);
        Assert.Equal(["org-a", "org-b"], state.Organisations.Select(o => o.Id).ToArray());
    }

    [Fact]
    public async Task Resolver_AccessibleSetBoundsResult()
    {
        // org-b is accessible; org-a is not. The accessible set is what bounds selection and the tree.
        var resolver = Resolver(
            new FakeComplianceStore { Assets = Assets() }, new RestrictedAssetAccess(Set("org-b")), cookie: "org-a");
        var state = await resolver.GetAsync();
        Assert.Null(state.SelectedId);
        Assert.Equal(Set("org-b"), state.AccessibleIds);
    }

    [Fact]
    public async Task Resolver_RepeatedReads_HitStoreOnce()
    {
        var store = new CountingComplianceStore { Assets = Assets() };
        var resolver = Resolver(store, new AllAssetAccess(), cookie: "org-a");
        await resolver.GetAsync();
        await resolver.GetAsync();
        Assert.Equal(1, store.AssetReads);
    }

    [Fact]
    public async Task Resolver_StoreFailure_DegradesToAllWithEmptyList()
    {
        var resolver = Resolver(new FakeComplianceStore { Unreachable = true }, new AllAssetAccess(), cookie: "org-a");
        var state = await resolver.GetAsync();
        Assert.Null(state.SelectedId);
        Assert.Empty(state.Organisations);
        Assert.Empty(state.AccessibleIds);
    }

    private static OrgSelectionResolver Resolver(IComplianceStore store, IAssetAccess access, string? cookie)
    {
        var context = new DefaultHttpContext();
        if (cookie is not null)
        {
            context.Request.Headers.Cookie = $"{OrgSelection.CookieName}={cookie}";
        }

        // The resolver takes its assets from the request cache's shared asset read, which carries the
        // assets alone unless the request has already taken an assurance snapshot to serve it from.
        var cache = new AuthzRequestCache(new FakeAuthzStore(), store);
        return new OrgSelectionResolver(new HttpContextAccessor { HttpContext = context }, cache, access);
    }

    private sealed class RestrictedAssetAccess(IReadOnlySet<string> accessible) : IAssetAccess
    {
        public ValueTask<IReadOnlySet<string>> AccessibleAssetIdsAsync(
            ClaimsPrincipal user, IReadOnlyList<AssetNode> assets, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(accessible);
    }

    private sealed class CountingComplianceStore : IComplianceStore
    {
        /// <summary>Reads of the shared asset list, which is what the resolver takes its assets from.</summary>
        public int AssetReads { get; private set; }

        public IReadOnlyList<AssetNode> Assets { get; init; } = [];

        public Task<IReadOnlyList<AssetNode>> GetAssetsAsync(CancellationToken cancellationToken = default)
        {
            AssetReads++;
            return Task.FromResult(Assets);
        }

        public Task<VendorAssuranceInputs> GetVendorAssuranceInputsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new VendorAssuranceInputs(Assets, []));

        public Task<IReadOnlyList<StandardRow>> GetStandardsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult((IReadOnlyList<StandardRow>)[]);

        public Task<IReadOnlyList<RequirementRow>> GetRequirementsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult((IReadOnlyList<RequirementRow>)[]);

        public Task<IReadOnlyList<ControlRow>> GetControlsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult((IReadOnlyList<ControlRow>)[]);

        public Task<IReadOnlyList<ScopeRow>> GetScopesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult((IReadOnlyList<ScopeRow>)[]);

        public Task<IReadOnlyList<CollectorRow>> GetCollectorsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult((IReadOnlyList<CollectorRow>)[]);

        public Task<IReadOnlyList<IntegrationConnectionRow>> GetIntegrationConnectionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult((IReadOnlyList<IntegrationConnectionRow>)[]);

        public Task<SoaInputs> GetStatementOfApplicabilityInputsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SoaInputs(Assets, [], []));

        public Task<SoaDrilldownInputs> GetStatementOfApplicabilityDrilldownInputsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SoaDrilldownInputs(Assets, [], [], [], []));

        public Task<ComplianceCounts> GetCountsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ComplianceCounts(0, 0, 0, 0, 0, 0, 0));
    }
}
