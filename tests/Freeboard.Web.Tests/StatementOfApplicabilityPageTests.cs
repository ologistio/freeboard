using System.Net;
using System.Security.Claims;
using Freeboard.Pages.Compliance;
using Freeboard.Persistence;
using Freeboard.Web;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Freeboard.Web.Tests;

/// <summary>
/// Server-rendered Statement of Applicability page: requires an authenticated user (an anonymous
/// browser is redirected to /login; the admin role is NOT required), renders the resolved node list
/// for a chosen standard ordered by id, is GET-only and served in GitOps read-only mode, and reads
/// through the injected <see cref="IComplianceStore"/> (no MySQL).
/// </summary>
public sealed class StatementOfApplicabilityPageTests
{
    private const string Path = "/compliance/statement-of-applicability";

    private static FakeComplianceStore PopulatedStore() => new()
    {
        Standards = [new StandardRow("std-a", "Standard A", "1.0", "Example Authority", null, null)],
        Organisations =
        [
            new OrganisationRow("org-a", "Org A", "Company", null),
            new OrganisationRow("org-eng", "Engineering", "Department", "org-a"),
        ],
        Scopes = [new ScopeRow("scope-a", "Scope A", "org-a", "std-a", "In")],
        Requirements =
        [
            new RequirementRow("req-a", "Requirement A", "std-a", "Theme", "Do the thing.", null, "L", "https://example.com/a"),
        ],
        RequirementScopes = [new RequirementScopeRow("rs-a", "Exclude req-a", "org-a", "req-a", "Out")],
    };

    // company -> dept -> team, plus a separate sibling company. std-a is explicit In at the company,
    // so the department inherits In from the company above it.
    private static FakeComplianceStore ScopedStore() => new()
    {
        Standards = [new StandardRow("std-a", "Standard A", "1.0", "Example Authority", null, null)],
        Organisations =
        [
            new OrganisationRow("org-co", "Company Co", "Company", null),
            new OrganisationRow("org-dept", "Department", "Department", "org-co"),
            new OrganisationRow("org-team", "Team", "Department", "org-dept"),
            new OrganisationRow("org-sib", "Sibling Co", "Company", null),
        ],
        Scopes = [new ScopeRow("scope-a", "Scope A", "org-co", "std-a", "In")],
    };

    private static AuthWebFactory Factory(FakeComplianceStore store, bool readOnly = false, IOrgAccess? orgAccess = null)
        => new() { Compliance = store, ReadOnly = readOnly, OrgAccess = orgAccess };

    private static HttpClient NoRedirectClient(AuthWebFactory factory)
        => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>A GET carrying a non-admin (member) session cookie, so the page auth is satisfied.</summary>
    private static async Task<HttpResponseMessage> GetAuthenticatedAsync(
        AuthWebFactory factory, HttpClient client, string relativeUrl, string? orgCookie = null)
    {
        var token = factory.SeedSession(AuthWebFactory.MakeUser("soa1"));
        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        var cookie = $"{SessionCookie.Name}={token}";
        if (orgCookie is not null)
        {
            cookie += $"; {OrgSelection.CookieName}={orgCookie}";
        }

        request.Headers.Add("Cookie", cookie);
        return await client.SendAsync(request);
    }

    private static bool HasNode(string html, string id) =>
        html.Contains($"data-node-id=\"{id}\"", StringComparison.Ordinal);

    /// <summary>
    /// The inner HTML of the SoA results table (class <c>soa-nodes</c>) only. Assertions about which
    /// orgs are in scope must target this region: the sidebar org selector legitimately lists every
    /// accessible org for navigation, so a whole-body check would match the nav, not the results.
    /// </summary>
    private static string ResultsTable(string html)
    {
        var start = html.IndexOf("<table class=\"soa-nodes", StringComparison.Ordinal);
        Assert.True(start >= 0, "SoA results table should be present");
        var end = html.IndexOf("</table>", start, StringComparison.Ordinal);
        Assert.True(end > start, "SoA results table should be closed");
        return html[start..end];
    }

    [Fact]
    public async Task AnonymousGetRedirectsToLogin()
    {
        using var factory = Factory(PopulatedStore());
        using var client = NoRedirectClient(factory);

        var response = await client.GetAsync($"{Path}?standard=std-a");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task RendersResolvedNodesForChosenStandard()
    {
        using var factory = Factory(PopulatedStore());
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, $"{Path}?standard=std-a");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        // Explicit In at org-a; org-eng inherits In. Ordered by id.
        var orgIndex = html.IndexOf("org-a", StringComparison.Ordinal);
        var engIndex = html.IndexOf("org-eng", StringComparison.Ordinal);
        Assert.True(orgIndex >= 0 && engIndex > orgIndex, "nodes should render ordered by id");
        Assert.Contains("explicit", html, StringComparison.Ordinal);
        Assert.Contains("inherited", html, StringComparison.Ordinal);
        Assert.Contains("In", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RendersPerRequirementExclusionsForInScopeNode()
    {
        using var factory = Factory(PopulatedStore());
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, $"{Path}?standard=std-a");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        // org-a excludes req-a (explicit); org-eng inherits the exclusion. Both render the deviation.
        Assert.Contains("data-requirement-id=\"req-a\"", html, StringComparison.Ordinal);
        Assert.Contains("req-a = Out", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoStandardChosenRendersSelectorWithoutNodes()
    {
        using var factory = Factory(PopulatedStore());
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, Path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Standard A", html, StringComparison.Ordinal); // selector option
        Assert.DoesNotContain("data-node-id", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServedInReadOnlyModeToAuthenticatedUser()
    {
        using var factory = Factory(PopulatedStore(), readOnly: true);
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, $"{Path}?standard=std-a");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UnreachableStoreRendersNoticeNot500()
    {
        using var factory = Factory(new FakeComplianceStore { Unreachable = true });
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, $"{Path}?standard=std-a");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("could not be reached", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectedOrganisationRendersOnlyItsSubtree()
    {
        using var factory = Factory(ScopedStore());
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, $"{Path}?standard=std-a", orgCookie: "org-co");
        var html = await response.Content.ReadAsStringAsync();

        Assert.True(HasNode(html, "org-co"));
        Assert.True(HasNode(html, "org-dept"));
        Assert.True(HasNode(html, "org-team"));
        Assert.False(HasNode(html, "org-sib"));
    }

    [Fact]
    public async Task SelectedDepartmentInheritsFromCompanyAbove()
    {
        using var factory = Factory(ScopedStore());
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, $"{Path}?standard=std-a", orgCookie: "org-dept");
        var html = await response.Content.ReadAsStringAsync();

        // The ancestor company is filtered out of the displayed subtree...
        Assert.False(HasNode(html, "org-co"));
        // ...but its In disposition still inherits into the selected department.
        var deptRow = html[html.IndexOf("data-node-id=\"org-dept\"", StringComparison.Ordinal)..];
        deptRow = deptRow[..deptRow.IndexOf("</tr>", StringComparison.Ordinal)];
        Assert.Contains("inherited", deptRow, StringComparison.Ordinal);
        Assert.Contains("In", deptRow, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllOrganisationsRendersEveryNode()
    {
        using var factory = Factory(ScopedStore());
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, $"{Path}?standard=std-a");
        var html = await response.Content.ReadAsStringAsync();

        Assert.True(HasNode(html, "org-co"));
        Assert.True(HasNode(html, "org-dept"));
        Assert.True(HasNode(html, "org-team"));
        Assert.True(HasNode(html, "org-sib"));
    }

    [Fact]
    public async Task OutOfScopeNodesAbsentFromScopedResults()
    {
        using var factory = Factory(ScopedStore());
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, $"{Path}?standard=std-a", orgCookie: "org-team");
        var table = ResultsTable(await response.Content.ReadAsStringAsync());

        // Only the selected subtree's node is in the results table; ancestors and siblings are not.
        Assert.Contains("data-node-id=\"org-team\"", table, StringComparison.Ordinal);
        Assert.DoesNotContain("data-node-id=\"org-co\"", table, StringComparison.Ordinal);
        Assert.DoesNotContain("data-node-id=\"org-dept\"", table, StringComparison.Ordinal);
        Assert.DoesNotContain("data-node-id=\"org-sib\"", table, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestrictedAccessKeepsOutOfAccessOrgsAbsentUnderAll()
    {
        var accessible = new HashSet<string>(StringComparer.Ordinal) { "org-co", "org-dept", "org-team" };
        using var factory = Factory(ScopedStore(), orgAccess: new SubsetOrgAccess(accessible));
        using var client = NoRedirectClient(factory);

        // No selection -> "All", but bounded by the accessible set, so the sibling never renders.
        var response = await GetAuthenticatedAsync(factory, client, $"{Path}?standard=std-a");
        var html = await response.Content.ReadAsStringAsync();

        Assert.True(HasNode(html, "org-co"));
        Assert.False(HasNode(html, "org-sib"));
    }

    [Fact]
    public async Task StandardQuerySurvivesSelectionFromPage()
    {
        using var factory = Factory(ScopedStore());
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, $"{Path}?standard=std-a");
        var html = await response.Content.ReadAsStringAsync();

        // The selector links carry the current page + query as return, so a selection preserves ?standard=.
        Assert.Contains("standard%3Dstd-a", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActiveScopeNamesSelectedOrganisation()
    {
        using var factory = Factory(ScopedStore());
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, $"{Path}?standard=std-a", orgCookie: "org-co");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("data-active-scope>Company Co", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActiveScopeNamesAllOrganisationsWithoutSelection()
    {
        using var factory = Factory(ScopedStore());
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, $"{Path}?standard=std-a");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("data-active-scope>All Organisations", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllReadsFailingRendersNoticeNotEmptyTable()
    {
        using var factory = Factory(new FakeComplianceStore { Unreachable = true });
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, $"{Path}?standard=std-a");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("could not be reached", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-node-id", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InputsLoadFailingAfterStandardsStillRendersNotice()
    {
        // The standards read succeeds, but the Statement-of-Applicability inputs read (which carries
        // the organisation list) throws: the page reads its organisations from its own inputs read,
        // so that read failing raises the notice - it does not take the layout resolver's degraded
        // empty list and render a healthy empty table.
        var store = ScopedStore();
        using var factory = Factory(new FakeComplianceStore
        {
            OrganisationsUnreachable = true,
            Standards = store.Standards,
            Scopes = store.Scopes,
        });
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, $"{Path}?standard=std-a");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("could not be reached", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-node-id", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstructorTakesOnlyComplianceStoreAndOrgAccess()
    {
        var ctor = Assert.Single(typeof(StatementOfApplicabilityModel).GetConstructors());
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToHashSet();

        Assert.Equal(new HashSet<Type> { typeof(IComplianceStore), typeof(IOrgAccess) }, paramTypes);
        Assert.DoesNotContain(typeof(OrgSelectionResolver), paramTypes);
    }

    private sealed class SubsetOrgAccess(IReadOnlySet<string> accessible) : IOrgAccess
    {
        public IReadOnlySet<string> AccessibleOrgIds(
            ClaimsPrincipal user, IReadOnlyList<OrganisationRow> organisations)
            => accessible;
    }
}
