using System.Net;
using System.Security.Claims;
using Freeboard.Persistence;
using Freeboard.Web;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Freeboard.Web.Tests;

/// <summary>
/// The layout organisation selector (view component). It renders on every authenticated layout page,
/// builds the accessible tree, marks the current selection, and degrades to only "All Organisations"
/// when the store is unreachable so an unrelated page never 500s. Driven through the authenticated
/// <c>/home</c> page, which renders the shared layout but does not itself read the compliance store.
/// </summary>
public sealed class OrgSelectorViewComponentTests
{
    private const string HomePath = "/home";

    private static AuthWebFactory Factory(FakeComplianceStore store, IAssetAccess? assetAccess = null)
        => new() { Compliance = store, AssetAccess = assetAccess };

    private static HttpClient NoRedirectClient(AuthWebFactory factory)
        => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<HttpResponseMessage> GetAsync(
        AuthWebFactory factory, HttpClient client, string url, string? orgCookie = null)
    {
        var token = factory.SeedSession(AuthWebFactory.MakeUser("orgvc"));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var cookie = $"{SessionCookie.Name}={token}";
        if (orgCookie is not null)
        {
            cookie += $"; {OrgSelection.CookieName}={orgCookie}";
        }

        request.Headers.Add("Cookie", cookie);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task TreeReflectsHierarchy()
    {
        using var factory = Factory(new FakeComplianceStore
        {
            Assets =
            [
                TestAssets.Org("org-a", title: "Org A"),
                TestAssets.Org("org-eng", "org-a", "Department", "Engineering"),
            ],
        });
        using var client = NoRedirectClient(factory);

        var response = await GetAsync(factory, client, HomePath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("Org A", html, StringComparison.Ordinal);
        Assert.Contains("Engineering", html, StringComparison.Ordinal);
        Assert.Contains("org=org-a", html, StringComparison.Ordinal);
        Assert.Contains("org=org-eng", html, StringComparison.Ordinal);
        // A parent with children exposes an expand/collapse toggle.
        Assert.Contains("Toggle Org A", html, StringComparison.Ordinal);
        // Each entry carries its kind so the view renders a differentiating company/department icon.
        Assert.Contains("data-kind=\"Company\"", html, StringComparison.Ordinal);
        Assert.Contains("data-kind=\"Department\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CurrentSelectionIsMarked()
    {
        using var factory = Factory(new FakeComplianceStore
        {
            Assets = [TestAssets.Org("org-a", title: "Org A")],
        });
        using var client = NoRedirectClient(factory);

        var response = await GetAsync(factory, client, HomePath, orgCookie: "org-a");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("aria-current=\"true\"", html, StringComparison.Ordinal);
        Assert.Contains("(current selection)", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectedNodePathIsExpandedOnLoad()
    {
        // Two root branches, one three levels deep. Selecting the deepest node must start its two
        // ancestor branches expanded (open: true) while the unrelated branch stays collapsed, so the
        // path down to a deep selection is unrolled in the picker on load.
        using var factory = Factory(new FakeComplianceStore
        {
            Assets =
            [
                TestAssets.Org("org-a", title: "Org A"),
                TestAssets.Org("org-eng", "org-a", "Department", "Engineering"),
                TestAssets.Org("org-team", "org-eng", "Department", "Platform"),
                TestAssets.Org("org-b", title: "Org B"),
                TestAssets.Org("org-sales", "org-b", "Department", "Sales"),
            ],
        });
        using var client = NoRedirectClient(factory);

        var response = await GetAsync(factory, client, HomePath, orgCookie: "org-team");
        var html = await response.Content.ReadAsStringAsync();

        // The deep node is rendered, and exactly the two ancestors (org-a, org-eng) start open while
        // the off-path branch (org-b) starts collapsed. org-team is a leaf and carries no toggle.
        Assert.Contains("org=org-team", html, StringComparison.Ordinal);
        Assert.Equal(2, Occurrences(html, "{ open: true }"));
        Assert.Equal(1, Occurrences(html, "{ open: false }"));
    }

    private static int Occurrences(string haystack, string needle)
        => haystack.Split(needle).Length - 1;

    [Fact]
    public async Task OnlyAccessibleOrganisationsAppear()
    {
        using var factory = Factory(
            new FakeComplianceStore
            {
                Assets =
                [
                    TestAssets.Org("org-a", title: "Org A"),
                    TestAssets.Org("org-b", title: "Org B"),
                ],
            },
            assetAccess: new SubsetAssetAccess(new HashSet<string>(StringComparer.Ordinal) { "org-a" }));
        using var client = NoRedirectClient(factory);

        // No cookie -> "All Organisations": org-b is out of the accessible set and must not render.
        var response = await GetAsync(factory, client, HomePath);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("org=org-a", html, StringComparison.Ordinal);
        Assert.DoesNotContain("org=org-b", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("m-1")]
    [InlineData("vendor-a")]
    public async Task NoNonOrganisationAssetBecomesASelectorEntry(string? orgCookie)
    {
        // The seam hands back an ASSET set, so a readable machine and vendor reach the selector. It
        // presents organisations only, and a cookie naming either falls back to "All Organisations".
        using var factory = Factory(
            new FakeComplianceStore
            {
                Assets =
                [
                    TestAssets.Org("org-a", title: "Org A"),
                    TestAssets.Machine("m-1", "org-a"),
                    TestAssets.Vendor("vendor-a", "org-a"),
                ],
            },
            assetAccess: new SubsetAssetAccess(
                new HashSet<string>(StringComparer.Ordinal) { "org-a", "m-1", "vendor-a" }));
        using var client = NoRedirectClient(factory);

        var response = await GetAsync(factory, client, HomePath, orgCookie);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("org=org-a", html, StringComparison.Ordinal);
        Assert.DoesNotContain("org=m-1", html, StringComparison.Ordinal);
        Assert.DoesNotContain("org=vendor-a", html, StringComparison.Ordinal);
        Assert.Contains("All Organisations", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnreachableStoreStillRendersLayoutWithAllOrganisationsOnly()
    {
        using var factory = Factory(new FakeComplianceStore { Unreachable = true });
        using var client = NoRedirectClient(factory);

        var response = await GetAsync(factory, client, HomePath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("All Organisations", html, StringComparison.Ordinal);
        // Degraded: no tree, so no per-org select links.
        Assert.DoesNotContain("?org=", html, StringComparison.Ordinal);
    }

    private sealed class SubsetAssetAccess(IReadOnlySet<string> accessible) : IAssetAccess
    {
        public ValueTask<IReadOnlySet<string>> AccessibleAssetIdsAsync(
            ClaimsPrincipal user, IReadOnlyList<AssetNode> assets, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlySet<string>>(accessible);
    }
}
