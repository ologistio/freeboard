using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Freeboard.Auth;
using Freeboard.Authz;
using Freeboard.Persistence;
using Freeboard.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Freeboard.Web.Tests;

/// <summary>
/// The shape of every narrowed compliance read, pinned surface by surface. The store API does not make
/// the composed straddle impossible - a caller can still take one snapshot of the assets and another of
/// its payload and pair them - so these tests are the enforcement, not a check on it.
///
/// Each case asserts three things about the surface under test: it takes exactly ONE snapshot, that
/// snapshot names EXACTLY the sets the decision needs, and the accessible set was resolved from THAT
/// snapshot's asset list (compared by reference, since the store hands a fresh list to every snapshot).
/// The set assertions are on the request's whole read sequence, so a surface cannot grow a second read
/// or an extra set without failing here. A surface that also makes an unnarrowed catalog read shows it
/// in the sequence: the catalog read is named explicitly rather than filtered out, so it cannot be used
/// to hide a narrowing read.
/// </summary>
public sealed class ComplianceSnapshotStructureTests
{
    private const string Api = "/api/v1/freeboard";

    private const ComplianceReadSet Assets = ComplianceReadSet.Assets;
    private const ComplianceReadSet Register = ComplianceReadSet.Assets | ComplianceReadSet.VendorAssurances;
    private const ComplianceReadSet Drilldown =
        ComplianceReadSet.Assets | ComplianceReadSet.Scopes | ComplianceReadSet.Requirements
        | ComplianceReadSet.Controls | ComplianceReadSet.Collectors;

    #region narrowed read endpoints

    [Theory]
    [InlineData("organisations", ComplianceReadSet.Assets)]
    [InlineData("scopes", ComplianceReadSet.Assets | ComplianceReadSet.Scopes)]
    [InlineData("vendors", ComplianceReadSet.Assets | ComplianceReadSet.VendorAssurances)]
    [InlineData("collectors", ComplianceReadSet.Assets | ComplianceReadSet.Collectors)]
    [InlineData("integration-connections", ComplianceReadSet.Assets | ComplianceReadSet.IntegrationConnections)]
    public async Task ANarrowedReadEndpointTakesOneSnapshotOfExactlyItsOwnSets(string route, ComplianceReadSet sets)
    {
        var store = Store();
        using var factory = new SnapshotWebFactory { Compliance = store };
        await BootAsync(factory, store);
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("m1"));

        var response = await client.GetAsync($"{Api}/{route}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reads = RequestReads(store);
        Assert.Equal([sets], reads.Sets);
        NarrowedFromSnapshot(factory, reads, 0);
    }

    [Fact]
    public async Task TheStatementOfApplicabilityEndpointNarrowsOnOneSnapshotBesideItsCatalogRead()
    {
        var store = Store();
        using var factory = new SnapshotWebFactory { Compliance = store };
        await BootAsync(factory, store);
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("m1"));

        var response = await client.GetAsync($"{Api}/statement-of-applicability/std-a");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The standards read decides not-found rather than visibility, so it stays outside the
        // projection snapshot. The projection itself is one read of exactly its three sets.
        var reads = RequestReads(store);
        Assert.Equal(
            [
                ComplianceReadSet.Standards,
                ComplianceReadSet.Assets | ComplianceReadSet.Scopes | ComplianceReadSet.Requirements,
            ],
            reads.Sets);
        NarrowedFromSnapshot(factory, reads, 1);
    }

    #endregion

    #region narrowing pages

    [Fact]
    public async Task TheVendorRegisterPageNarrowsOnOneSnapshotBesideItsCatalogRead()
    {
        var store = Store();
        using var factory = new SnapshotWebFactory { Compliance = store };
        await BootAsync(factory, store);

        var response = await GetPageAsync(factory, "/compliance/vendors");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The rail and the organisation selector are both served from the page's snapshot, so the page
        // read is the request's only asset-bearing read. The standards read supplies assurance titles.
        var reads = RequestReads(store);
        Assert.Equal(
            [
                ComplianceReadSet.Assets | ComplianceReadSet.Scopes | ComplianceReadSet.VendorAssurances,
                ComplianceReadSet.Standards,
            ],
            reads.Sets);
        NarrowedFromSnapshot(factory, reads, 0);
    }

    [Fact]
    public async Task TheCollectorRegisterPageNarrowsOnOneSnapshotBesideItsCatalogRead()
    {
        var store = Store();
        using var factory = new SnapshotWebFactory { Compliance = store };
        await BootAsync(factory, store);

        var response = await GetPageAsync(factory, "/settings/collectors");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The controls are unnarrowed reference data the page groups its rows under, so they stay
        // outside. The trailing read is the rail's, whose sets the page's snapshot does not cover.
        var reads = RequestReads(store);
        Assert.Equal(
            [ComplianceReadSet.Controls, ComplianceReadSet.Assets | ComplianceReadSet.Collectors, Register],
            reads.Sets);
        NarrowedFromSnapshot(factory, reads, 1);
    }

    [Fact]
    public async Task TheIntegrationConnectionPageNarrowsOnOneSnapshot()
    {
        var store = Store();
        using var factory = new SnapshotWebFactory { Compliance = store };
        await BootAsync(factory, store);

        var response = await GetPageAsync(factory, "/settings/integration-connections");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reads = RequestReads(store);
        Assert.Equal(
            [ComplianceReadSet.Assets | ComplianceReadSet.IntegrationConnections, Register],
            reads.Sets);
        NarrowedFromSnapshot(factory, reads, 0);
    }

    [Theory]
    [InlineData("/compliance/statement-of-applicability?standard=std-a")]
    [InlineData("/compliance/control-detail?standard=std-a&org=org-a&requirement=req-a&control=ctrl-a")]
    public async Task ADrilldownPageNarrowsOnOneSnapshotBesideItsCatalogRead(string url)
    {
        var store = Store();
        using var factory = new SnapshotWebFactory { Compliance = store };
        await BootAsync(factory, store);

        var response = await GetPageAsync(factory, url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reads = RequestReads(store);
        Assert.Equal([ComplianceReadSet.Standards, Drilldown, Register], reads.Sets);
        NarrowedFromSnapshot(factory, reads, 1);
    }

    #endregion

    #region shell surfaces

    [Fact]
    public async Task TheNavRailTakesOneSnapshotOfTheAssetsAndTheAssurancesAndNarrowsFromIt()
    {
        // Driven through a page whose own snapshot does NOT cover the rail's sets, so the rail takes its
        // own read and this asserts the rail's shape rather than the page's.
        var store = Store();
        using var factory = new SnapshotWebFactory { Compliance = store };
        await BootAsync(factory, store);

        var response = await GetPageAsync(factory, "/settings/integration-connections");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reads = RequestReads(store);
        Assert.Equal([ComplianceReadSet.Assets | ComplianceReadSet.IntegrationConnections, Register], reads.Sets);

        // Order-independent: exactly one accessible set was resolved over the rail's snapshot, and it is
        // the rail's. A rail reading its rows from one snapshot and its assets from another would resolve
        // over some other list and leave this one unused.
        Assert.Single(factory.Access.NarrowedWith, l => ReferenceEquals(l, reads.Assets[1]));
    }

    [Fact]
    public async Task TheOrganisationSelectorTakesOneSnapshotOfTheAssetsAndNarrowsFromIt()
    {
        var store = Store();
        var access = new RecordingAssetAccess(new AllAssetAccess(), new AssetAccessRecords());
        var cache = new AuthzRequestCache(new FakeAuthzStore(), store);
        var context = new DefaultHttpContext();
        var resolver = new OrgSelectionResolver(new HttpContextAccessor { HttpContext = context }, cache, access);

        var state = await resolver.GetAsync();

        Assert.Contains("org-a", state.AccessibleIds);
        Assert.Equal([Assets], store.SnapshotReads);
        Assert.Same(store.ServedAssets[0], Assert.Single(access.Records.NarrowedWith));
    }

    #endregion

    #region ingest admission

    [Fact]
    public async Task TheIngestAdmissionCheckTakesOneSnapshotOfItsFiveSets()
    {
        // Not an accessible-set narrowing - the caller is a collector credential - but it is one composed
        // decision, so it takes one snapshot like every other.
        var store = Store();
        using var factory = new SnapshotWebFactory { Compliance = store };
        await BootAsync(factory, store);
        var token = factory.SeedCollectorCredential("coll-a");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var content = new StringContent(
            """
            {
              "schema_version": "freeboard.evidence.v1",
              "collector_id": "coll-a",
              "organisation_id": "org-a",
              "requirement_id": "req-a",
              "run_id": "run-1",
              "collected_at": "2026-01-01T00:00:00Z",
              "checks": [{"name":"c1","severity":"hard","result":"pass"}]
            }
            """,
            Encoding.UTF8,
            "application/json");

        var response = await client.PostAsync($"{Api}/evidence", content);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal([Drilldown], RequestReads(store).Sets);
    }

    #endregion

    #region snapshot reuse

    [Fact]
    public async Task TheRegisterPageSnapshotIsTakenBeforeTheRailAsksSoTheRailMakesNoReadOfItsOwn()
    {
        // The ordering is a fact of Razor Pages, not a hope: the handler runs to completion before the
        // view executes, and the rail is a view component in the layout. Asserted directly so a refactor
        // that moved the rail's read ahead of the handler fails here rather than costing a round trip.
        var store = Store();
        // A lapsed assurance, so the rail renders its badge. The badge is counted over the rail's own
        // accessible set, which is what proves the rail ran rather than being skipped.
        store.Assurances = [new VendorAssuranceRow("vendor-a", "std-a", new DateOnly(2020, 1, 1), null)];
        using var factory = new SnapshotWebFactory { Compliance = store };
        await BootAsync(factory, store);

        var response = await GetPageAsync(factory, "/compliance/vendors");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("fb-navcount", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var reads = RequestReads(store);
        var page = ComplianceReadSet.Assets | ComplianceReadSet.Scopes | ComplianceReadSet.VendorAssurances;
        Assert.Equal([page, ComplianceReadSet.Standards], reads.Sets);

        // The page's snapshot covers the rail's ask and the selector's, so every decision in the render
        // narrows with one asset list and shares ONE accessible set - the same instance, not an equal one.
        var resolved = factory.Access.Resolved;
        Assert.All(factory.Access.NarrowedWith, l => Assert.Same(reads.Assets[0], l));
        Assert.All(resolved, s => Assert.Same(resolved[0], s));
    }

    [Fact]
    public async Task TwoSnapshotsAreNeverMergedIntoASyntheticWiderOne()
    {
        // The negative that keeps reuse honest. A merged snapshot's lists would come from two
        // transactions, which is exactly the straddle, so an ask no single taken snapshot covers must
        // read again rather than be answered from the union of what the request already holds.
        var store = Store();
        var cache = new AuthzRequestCache(new FakeAuthzStore(), store);

        var scopes = await cache.GetSnapshotAsync(ComplianceReadSet.Assets | ComplianceReadSet.Scopes);
        var assurances = await cache.GetSnapshotAsync(Register);

        Assert.Equal([ComplianceReadSet.Assets | ComplianceReadSet.Scopes, Register], store.SnapshotReads);
        Assert.NotSame(scopes, assurances);
        Assert.NotSame(scopes.Assets, assurances.Assets);
        Assert.Equal(Register, assurances.Sets);
        // The second snapshot carries only what it named: the merge would have brought the scopes with it.
        Assert.Throws<ComplianceReadSetNotRequestedException>(() => assurances.Scopes);
    }

    #endregion

    #region fixtures

    private static FakeComplianceStore Store() => SnapshotReadProbe.Store();

    private static Task BootAsync(AuthWebFactory factory, FakeComplianceStore store)
        => SnapshotReadProbe.BootAsync(factory, store);

    private static RequestSnapshots RequestReads(FakeComplianceStore store)
        => SnapshotReadProbe.RequestReads(store);

    private static async Task<HttpResponseMessage> GetPageAsync(AuthWebFactory factory, string url)
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var token = factory.SeedSession(AuthWebFactory.MakeUser("p1"));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", $"{SessionCookie.Name}={token}");
        return await client.SendAsync(request);
    }

    /// <summary>
    /// Asserts the accessible set the surface narrowed with was resolved from the asset list of the
    /// snapshot at <paramref name="index"/> - the one its rows came from - rather than from a separately
    /// read list. The surface's own resolution is the request's first, because a page handler runs to
    /// completion before the shell around it renders.
    /// </summary>
    private static void NarrowedFromSnapshot(SnapshotWebFactory factory, RequestSnapshots reads, int index)
        => Assert.Same(reads.Assets[index], factory.Access.NarrowedWith[0]);

    #endregion
}

/// <summary>The reads one request made, with the asset list each snapshot was served with.</summary>
internal sealed record RequestSnapshots(
    IReadOnlyList<ComplianceReadSet> Sets, IReadOnlyList<IReadOnlyList<AssetNode>?> Assets);

/// <summary>Fixture and read-sequence helpers shared by the surface and gate structural tests.</summary>
internal static class SnapshotReadProbe
{
    /// <summary>
    /// The app reads once at boot: the startup token-resolvability warning names the collectors and the
    /// connections. It runs on a background task off the request path, so a test waits for it and then
    /// asserts on the reads its own request made.
    /// </summary>
    private const int BootReads = 1;

    /// <summary>How long the boot read may take before the wait is called a failure rather than a hang.</summary>
    private static readonly TimeSpan BootTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// One store seeding every list, so a surface that reads a set it should not name shows up as an
    /// extra set rather than as an empty render.
    /// </summary>
    public static FakeComplianceStore Store() => new()
    {
        Standards = [new StandardRow("std-a", "Standard A", "1.0", "Example Authority", null, null)],
        Requirements =
        [
            new RequirementRow("req-a", "Requirement A", "std-a", "Theme", "Do the thing.", null, "L", "https://x/a"),
        ],
        Controls = [new ControlRow("ctrl-a", "Control A", ["req-a"], "all")],
        Assets = [TestAssets.Org("org-a", title: "Org A"), TestAssets.Vendor("vendor-a", "org-a", title: "Vendor A")],
        Scopes = [new ScopeRow("scope-a", "Scope A", "org-a", "std-a", null, null, "In", null)],
        Collectors =
        [
            new CollectorRow(
                "coll-a", "Collector A", "ctrl-a", "vendor-a", "integration", "fleet", "daily", null,
                CollectorConfigView.Empty),
        ],
        Connections = [new IntegrationConnectionRow("conn-a", "fleet", "https://x", "daily", "vendor-a")],
        Assurances = [new VendorAssuranceRow("vendor-a", "std-a", new DateOnly(2030, 1, 1), null)],
    };

    public static async Task BootAsync(WebApplicationFactory<Program> factory, FakeComplianceStore store)
    {
        _ = factory.Services;
        await store.FirstRead.WaitAsync(BootTimeout);

        // Exact, so a second boot read is a failure here rather than something silently absorbed into
        // every surface's expected sequence.
        Assert.Equal(
            [ComplianceReadSet.Collectors | ComplianceReadSet.IntegrationConnections], store.SnapshotReads);
    }

    public static RequestSnapshots RequestReads(FakeComplianceStore store)
        => new([.. store.SnapshotReads.Skip(BootReads)], [.. store.ServedAssets.Skip(BootReads)]);
}

/// <summary>
/// The reads of one run of the accessibility seam: the asset list each resolution was handed, and the
/// set each returned. Both are compared by reference, so a test can tell one snapshot's list from
/// another's and one memoized accessible set from a re-resolved equal one.
/// </summary>
internal sealed class AssetAccessRecords
{
    public List<IReadOnlyList<AssetNode>> NarrowedWith { get; } = [];

    public List<IReadOnlySet<string>> Resolved { get; } = [];
}

/// <summary>
/// Records what the accessibility seam was asked and what it answered, then delegates. It DECORATES the
/// real seam rather than replacing it, because the accessible set is memoized per asset list inside
/// that seam and a replacement would hide the sharing these tests assert.
/// </summary>
internal sealed class RecordingAssetAccess(IAssetAccess inner, AssetAccessRecords records) : IAssetAccess
{
    public AssetAccessRecords Records => records;

    public async ValueTask<IReadOnlySet<string>> AccessibleAssetIdsAsync(
        ClaimsPrincipal user, IReadOnlyList<AssetNode> assets, CancellationToken cancellationToken = default)
    {
        records.NarrowedWith.Add(assets);
        var accessible = await inner.AccessibleAssetIdsAsync(user, assets, cancellationToken);
        records.Resolved.Add(accessible);
        return accessible;
    }
}

/// <summary>The app as <see cref="AuthWebFactory"/> boots it, with the accessibility seam recorded.</summary>
internal sealed class SnapshotWebFactory : AuthWebFactory
{
    public AssetAccessRecords Access { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureTestServices(services =>
        {
            services.AddScoped<AuthzAssetAccess>();
            services.RemoveAll<IAssetAccess>();
            services.AddScoped<IAssetAccess>(sp =>
                new RecordingAssetAccess(sp.GetRequiredService<AuthzAssetAccess>(), Access));
        });
    }
}
