using System.Net;
using System.Security.Claims;
using Freeboard.Auth;
using Freeboard.Persistence;
using Freeboard.Web;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Freeboard.Web.Tests;

/// <summary>
/// The full-page control detail (O4): it renders the same shared anatomy the drawer shows for the same
/// control (facet parity, actions slot excluded), raises the store-unreachable notice, authorizes on the
/// caller's full accessible org set rather than the active-scope cookie, and returns not-found without
/// leaking a record name for a missing or inaccessible control.
/// </summary>
public sealed class ControlDetailPageTests
{
    private const string SoaPath = "/compliance/statement-of-applicability";
    private const string DetailPath = "/compliance/control-detail";

    private static HttpClient NoRedirectClient(AuthWebFactory factory)
        => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<HttpResponseMessage> GetAsync(
        AuthWebFactory factory, HttpClient client, string url, string? orgCookie = null)
    {
        var token = factory.SeedSession(AuthWebFactory.MakeUser("cd1"));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var cookie = $"{SessionCookie.Name}={token}";
        if (orgCookie is not null)
        {
            cookie += $"; {OrgSelection.CookieName}={orgCookie}";
        }

        request.Headers.Add("Cookie", cookie);
        return await client.SendAsync(request);
    }

    // org-a is In (default). ctrl-a maps to req-a with one collector check seeded Passing.
    private static FakeComplianceStore SingleOrgStore() => new()
    {
        Standards = [new StandardRow("std-a", "Standard A", "1.0", "Example Authority", null, null)],
        Organisations = [new OrganisationRow("org-a", "Org A", "Company", null)],
        Requirements =
        [
            new RequirementRow("req-a", "Requirement A", "std-a", "Theme", "Do the thing.", null, "L", "https://example.com/a"),
        ],
        Controls = [new ControlRow("ctrl-a", "Control A", ["req-a"], "all")],
        Collectors =
        [
            new EvidenceCollectorRow("coll-a", "Collector A", "ctrl-a", null, "integration", "daily", null, new Dictionary<string, string>()),
        ],
    };

    // Two sibling root orgs, both In by default, both carrying ctrl-a under req-a.
    private static FakeComplianceStore TwoOrgStore() => new()
    {
        Standards = [new StandardRow("std-a", "Standard A", "1.0", "Example Authority", null, null)],
        Organisations =
        [
            new OrganisationRow("org-a", "Org A", "Company", null),
            new OrganisationRow("org-b", "Org B", "Company", null),
        ],
        Requirements =
        [
            new RequirementRow("req-a", "Requirement A", "std-a", "Theme", "Do the thing.", null, "L", "https://example.com/a"),
        ],
        Controls = [new ControlRow("ctrl-a", "Control A", ["req-a"], "all")],
    };

    private static FakeEvidenceStore PassingEvidence() => new FakeEvidenceStore()
        .AddCollectorRun("org-a", "req-a", "coll-a", "daily", DateTime.UtcNow, ("Hard", "Pass"));

    // A daily collector last collected 30 days ago: past its cadence window, so the store derives Stale
    // even though its checks pass (no hard failure).
    private static FakeEvidenceStore StaleEvidence() => new FakeEvidenceStore()
        .AddCollectorRun("org-a", "req-a", "coll-a", "daily", DateTime.UtcNow.AddDays(-30), ("Hard", "Pass"));

    // The anatomy head-through-body region (eyebrow through history), excluding the context-dependent
    // actions foot, so the parity assertion covers the facet content the drawer and full page must share.
    private static string FacetRegion(string html)
    {
        var start = html.IndexOf("<div class=\"fb-dhead\">", StringComparison.Ordinal);
        Assert.True(start >= 0, "anatomy head should be present");
        var end = html.IndexOf("<div class=\"fb-dfoot\">", start, StringComparison.Ordinal);
        Assert.True(end > start, "anatomy foot should follow the body");
        return html[start..end];
    }

    [Fact]
    public async Task DrawerTemplateAndFullPageRenderIdenticalFacetAnatomy()
    {
        var url = $"{DetailPath}?standard=std-a&org=org-a&requirement=req-a&control=ctrl-a";

        using var factory = new AuthWebFactory { Compliance = SingleOrgStore(), EvidenceReads = PassingEvidence() };
        using var client = NoRedirectClient(factory);

        var soa = await (await GetAsync(factory, client, $"{SoaPath}?standard=std-a")).Content.ReadAsStringAsync();
        var full = await (await GetAsync(factory, client, url)).Content.ReadAsStringAsync();

        var soaFacets = FacetRegion(soa);
        var fullFacets = FacetRegion(full);

        // Same partial, same projected model, so the eyebrow-through-history content is byte-identical.
        Assert.Equal(soaFacets, fullFacets);

        // And it carries the sidecar-fed per-check status (Passing), not just the projection facets - the
        // control-level status stays an honest empty (S6), never a fabricated pass.
        Assert.Contains("Passing", fullFacets, StringComparison.Ordinal);
        Assert.Contains("req-a Requirement A", fullFacets, StringComparison.Ordinal);
        // The control-level status stays an honest empty (S6), never a fabricated pass.
        Assert.Contains("Not evaluated", fullFacets, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FullPageRendersControlAnatomy()
    {
        using var factory = new AuthWebFactory { Compliance = SingleOrgStore(), EvidenceReads = PassingEvidence() };
        using var client = NoRedirectClient(factory);

        var response = await GetAsync(
            factory, client, $"{DetailPath}?standard=std-a&org=org-a&requirement=req-a&control=ctrl-a");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("data-control-detail", html, StringComparison.Ordinal);
        Assert.Contains("id=\"fb-detail-title\"", html, StringComparison.Ordinal);
        Assert.Contains("Control A", html, StringComparison.Ordinal);
        // The full page's action is a back-link, never a self-link or a fabricated mutating action.
        Assert.Contains("Back to Statement of Applicability", html, StringComparison.Ordinal);
        // N9: the back-link carries the originating standard so returning restores the same control tree,
        // not the empty no-standard list.
        Assert.Contains($"href=\"{SoaPath}?standard=std-a\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BreadcrumbLeafLinksToFullCurrentUrlIncludingQuery()
    {
        // N8: the current-page crumb is a working self-link. The control detail 404s without its query
        // params, so its leaf must carry the full URL, not the bare path.
        const string query = "standard=std-a&org=org-a&requirement=req-a&control=ctrl-a";

        using var factory = new AuthWebFactory { Compliance = SingleOrgStore(), EvidenceReads = PassingEvidence() };
        using var client = NoRedirectClient(factory);

        var response = await GetAsync(factory, client, $"{DetailPath}?{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        var encodedQuery = query.Replace("&", "&amp;", StringComparison.Ordinal);
        Assert.Contains(
            $"<a href=\"{DetailPath}?{encodedQuery}\" aria-current=\"page\">Control A</a>", html, StringComparison.Ordinal);
        Assert.DoesNotContain(
            $"<a href=\"{DetailPath}\" aria-current=\"page\">", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StaleProvingCheckRendersDegradedSealAndNamesStaleWhileUnknownStaysNoteOnly()
    {
        var url = $"{DetailPath}?standard=std-a&org=org-a&requirement=req-a&control=ctrl-a";

        using var staleFactory = new AuthWebFactory { Compliance = SingleOrgStore(), EvidenceReads = StaleEvidence() };
        using var staleClient = NoRedirectClient(staleFactory);
        var staleRegion = FacetRegion(await (await GetAsync(staleFactory, staleClient, url)).Content.ReadAsStringAsync());

        // S6: a stale check degrades to the warn (Drifting) seal - never a bare note, never passing - and
        // the note names the stale state.
        Assert.Contains("<span class=\"fb-status warn\">", staleRegion, StringComparison.Ordinal);
        Assert.Contains("Drifting", staleRegion, StringComparison.Ordinal);
        Assert.Contains("Collection stopped", staleRegion, StringComparison.Ordinal);
        Assert.DoesNotContain("Passing", staleRegion, StringComparison.Ordinal);

        using var unknownFactory = new AuthWebFactory { Compliance = SingleOrgStore() };
        using var unknownClient = NoRedirectClient(unknownFactory);
        var unknownRegion = FacetRegion(await (await GetAsync(unknownFactory, unknownClient, url)).Content.ReadAsStringAsync());

        // Unknown stays note-only: the honest "Not collected" note, no seal - and so renders differently
        // from Stale.
        Assert.Contains("Not collected", unknownRegion, StringComparison.Ordinal);
        Assert.DoesNotContain("Drifting", unknownRegion, StringComparison.Ordinal);
        Assert.NotEqual(staleRegion, unknownRegion);
    }

    [Fact]
    public async Task UnreachableStoreRendersNoticeNot500()
    {
        using var factory = new AuthWebFactory { Compliance = new FakeComplianceStore { Unreachable = true } };
        using var client = NoRedirectClient(factory);

        var response = await GetAsync(
            factory, client, $"{DetailPath}?standard=std-a&org=org-a&requirement=req-a&control=ctrl-a");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("could not be reached", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingControlReturnsNotFoundWithoutLeakingNames()
    {
        using var factory = new AuthWebFactory { Compliance = SingleOrgStore() };
        using var client = NoRedirectClient(factory);

        var response = await GetAsync(
            factory, client, $"{DetailPath}?standard=std-a&org=org-a&requirement=req-a&control=ctrl-missing");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Control A", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AccessibleOrgRendersRegardlessOfActiveScopeCookie()
    {
        // The active-scope cookie selects org-a, but a direct link for the equally accessible org-b must
        // still render: reachability binds to the accessible set, not the transient scope selection.
        using var factory = new AuthWebFactory { Compliance = TwoOrgStore() };
        using var client = NoRedirectClient(factory);

        var response = await GetAsync(
            factory, client, $"{DetailPath}?standard=std-a&org=org-b&requirement=req-a&control=ctrl-a",
            orgCookie: "org-a");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Control A", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrgOutsideAccessibleSetReturnsNotFound()
    {
        var accessible = new HashSet<string>(StringComparer.Ordinal) { "org-a" };
        using var factory = new AuthWebFactory { Compliance = TwoOrgStore(), OrgAccess = new SubsetOrgAccess(accessible) };
        using var client = NoRedirectClient(factory);

        var response = await GetAsync(
            factory, client, $"{DetailPath}?standard=std-a&org=org-b&requirement=req-a&control=ctrl-a");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Control A", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstructorTakesComplianceStoreOrgAccessAndEvidenceStore()
    {
        var ctor = Assert.Single(typeof(Freeboard.Pages.Compliance.ControlDetailModel).GetConstructors());
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToHashSet();

        Assert.Equal(
            new HashSet<Type> { typeof(IComplianceStore), typeof(IOrgAccess), typeof(IEvidenceStore) }, paramTypes);
    }

    private sealed class SubsetOrgAccess(IReadOnlySet<string> accessible) : IOrgAccess
    {
        public ValueTask<IReadOnlySet<string>> AccessibleOrgIdsAsync(
            ClaimsPrincipal user, IReadOnlyList<OrganisationRow> organisations, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(accessible);
    }
}
