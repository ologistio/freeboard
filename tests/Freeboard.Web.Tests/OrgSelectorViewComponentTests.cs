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

    private static AuthWebFactory Factory(FakeComplianceStore store, IOrgAccess? orgAccess = null)
        => new() { Compliance = store, OrgAccess = orgAccess };

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
            Organisations =
            [
                new OrganisationRow("org-a", "Org A", "Company", null),
                new OrganisationRow("org-eng", "Engineering", "Department", "org-a"),
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
            Organisations = [new OrganisationRow("org-a", "Org A", "Company", null)],
        });
        using var client = NoRedirectClient(factory);

        var response = await GetAsync(factory, client, HomePath, orgCookie: "org-a");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("aria-current=\"true\"", html, StringComparison.Ordinal);
        Assert.Contains("(current selection)", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyAccessibleOrganisationsAppear()
    {
        using var factory = Factory(
            new FakeComplianceStore
            {
                Organisations =
                [
                    new OrganisationRow("org-a", "Org A", "Company", null),
                    new OrganisationRow("org-b", "Org B", "Company", null),
                ],
            },
            orgAccess: new SubsetOrgAccess(new HashSet<string>(StringComparer.Ordinal) { "org-a" }));
        using var client = NoRedirectClient(factory);

        // No cookie -> "All Organisations": org-b is out of the accessible set and must not render.
        var response = await GetAsync(factory, client, HomePath);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("org=org-a", html, StringComparison.Ordinal);
        Assert.DoesNotContain("org=org-b", html, StringComparison.Ordinal);
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

    private sealed class SubsetOrgAccess(IReadOnlySet<string> accessible) : IOrgAccess
    {
        public IReadOnlySet<string> AccessibleOrgIds(
            ClaimsPrincipal user, IReadOnlyList<OrganisationRow> organisations)
            => accessible;
    }
}
