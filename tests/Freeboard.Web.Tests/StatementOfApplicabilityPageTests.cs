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

        var table = ResultsTable(await response.Content.ReadAsStringAsync());
        // org-a excludes req-a (explicit); org-eng inherits it. The requirement disclosure row carries the
        // hook, an Out badge, and its provenance - both explicit and inherited appear across the two nodes.
        Assert.Contains("data-requirement-id=\"req-a\"", table, StringComparison.Ordinal);
        Assert.Contains("badge-danger", table, StringComparison.Ordinal);
        Assert.Contains("explicit", table, StringComparison.Ordinal);
        Assert.Contains("inherited", table, StringComparison.Ordinal);
    }

    // org-a is In. req-a stays In (default) and carries ctrl-a with one collector and one template;
    // req-b is excluded Out, so it is a leaf even though ctrl-b maps to it. The collector names vendor-x,
    // seeded as a vendor so the check row shows the vendor title, not the raw id.
    private static FakeComplianceStore DrilldownStore() => new()
    {
        Standards = [new StandardRow("std-a", "Standard A", "1.0", "Example Authority", null, null)],
        Organisations = [new OrganisationRow("org-a", "Org A", "Company", null)],
        Scopes = [new ScopeRow("scope-a", "Scope A", "org-a", "std-a", "In")],
        Requirements =
        [
            new RequirementRow("req-a", "Requirement A", "std-a", "Theme", "Do the thing.", null, "L", "https://example.com/a"),
            new RequirementRow("req-b", "Requirement B", "std-a", "Theme", "Do another thing.", null, "L", "https://example.com/b"),
        ],
        RequirementScopes = [new RequirementScopeRow("rs-b", "Exclude req-b", "org-a", "req-b", "Out")],
        Controls =
        [
            new ControlRow("ctrl-a", "Control A", ["req-a"], "all"),
            new ControlRow("ctrl-b", "Control B", ["req-b"], null),
        ],
        Collectors =
        [
            new EvidenceCollectorRow("coll-a", "Collector A", "ctrl-a", "vendor-x", "integration", "daily", null, new Dictionary<string, string>()),
        ],
        Templates =
        [
            new AttestationTemplateRow("tmpl-a", "Template A", "ctrl-a", "manual", null, [], null, []),
        ],
        Vendors = [new VendorRow("vendor-x", "Vendor X")],
    };

    [Fact]
    public async Task RendersEveryDrilldownHookInSsrHtml()
    {
        using var factory = Factory(DrilldownStore());
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, $"{Path}?standard=std-a");
        var table = ResultsTable(await response.Content.ReadAsStringAsync());

        Assert.Contains("data-node-id=\"org-a\"", table, StringComparison.Ordinal);
        // The In requirement expands to its control and both check kinds.
        Assert.Contains("data-requirement-id=\"req-a\"", table, StringComparison.Ordinal);
        Assert.Contains("data-control-id=\"ctrl-a\"", table, StringComparison.Ordinal);
        Assert.Contains("data-check-id=\"coll-a\"", table, StringComparison.Ordinal);
        Assert.Contains("data-check-id=\"tmpl-a\"", table, StringComparison.Ordinal);
        // Both check kinds render, tagged by kind.
        Assert.Contains("data-check-kind=\"collector\"", table, StringComparison.Ordinal);
        Assert.Contains("data-check-kind=\"attestation\"", table, StringComparison.Ordinal);
        // The collector's vendor shows by title, not the raw id.
        Assert.Contains("Vendor X", table, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExcludedRequirementRendersAsLeafWithoutControls()
    {
        using var factory = Factory(DrilldownStore());
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, $"{Path}?standard=std-a");
        var table = ResultsTable(await response.Content.ReadAsStringAsync());

        // req-b is excluded (Out): its row renders with the danger badge and provenance...
        var reqB = table[table.IndexOf("data-requirement-id=\"req-b\"", StringComparison.Ordinal)..];
        reqB = reqB[..reqB.IndexOf("</li>", StringComparison.Ordinal)];
        Assert.Contains("badge-danger", reqB, StringComparison.Ordinal);
        Assert.Contains("explicit", reqB, StringComparison.Ordinal);
        // ...but it is a leaf: no expand toggle and no nested control row, even though ctrl-b maps to it.
        Assert.DoesNotContain("Toggle requirement req-b", reqB, StringComparison.Ordinal);
        Assert.DoesNotContain("data-control-id=\"ctrl-b\"", table, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NestedRowsRenderCollapsedByDefault()
    {
        using var factory = Factory(DrilldownStore());
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, $"{Path}?standard=std-a");
        var table = ResultsTable(await response.Content.ReadAsStringAsync());

        // Every disclosure scope defaults closed; nested content stays in the DOM but hidden via x-cloak.
        Assert.Contains("x-data=\"{ open: false }\"", table, StringComparison.Ordinal);
        Assert.Contains("x-cloak", table, StringComparison.Ordinal);
        Assert.DoesNotContain("open: true", table, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutOfScopeNodeHasNoRequirementChildren()
    {
        var store = new FakeComplianceStore
        {
            Standards = [new StandardRow("std-a", "Standard A", "1.0", "Example Authority", null, null)],
            Organisations = [new OrganisationRow("org-a", "Org A", "Company", null)],
            Scopes = [new ScopeRow("scope-a", "Scope A", "org-a", "std-a", "Out")],
            Requirements =
            [
                new RequirementRow("req-a", "Requirement A", "std-a", "Theme", "Do the thing.", null, "L", "https://example.com/a"),
            ],
        };
        using var factory = Factory(store);
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, $"{Path}?standard=std-a");
        var table = ResultsTable(await response.Content.ReadAsStringAsync());

        Assert.Contains("data-node-id=\"org-a\"", table, StringComparison.Ordinal);
        // Standard Out dominates: the node carries no requirement children.
        Assert.DoesNotContain("data-requirement-id", table, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DefaultedInNodeRendersInScope()
    {
        // A store with no Scope rows: every node defaults In marked "default" under opt-out.
        var store = new FakeComplianceStore
        {
            Standards = [new StandardRow("std-a", "Standard A", "1.0", "Example Authority", null, null)],
            Organisations = [new OrganisationRow("org-a", "Org A", "Company", null)],
        };
        using var factory = Factory(store);
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, $"{Path}?standard=std-a");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var table = ResultsTable(await response.Content.ReadAsStringAsync());
        var row = table[table.IndexOf("data-node-id=\"org-a\"", StringComparison.Ordinal)..];
        Assert.Contains("badge-success", row, StringComparison.Ordinal);
        Assert.Contains(">In<", row, StringComparison.Ordinal);
        Assert.Contains("default", row, StringComparison.Ordinal);
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
    public async Task UnknownStandardRendersNotFoundNoticeWithoutNodes()
    {
        using var factory = Factory(PopulatedStore());
        using var client = NoRedirectClient(factory);

        // A ?standard= that names no persisted standard must not render an all-In table; under opt-out
        // that would present a typo or deleted standard as applicable to every org.
        var response = await GetAuthenticatedAsync(factory, client, $"{Path}?standard=std-does-not-exist");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("data-standard-not-found", html, StringComparison.Ordinal);
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
        public ValueTask<IReadOnlySet<string>> AccessibleOrgIdsAsync(
            ClaimsPrincipal user, IReadOnlyList<OrganisationRow> organisations, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlySet<string>>(accessible);
    }
}
