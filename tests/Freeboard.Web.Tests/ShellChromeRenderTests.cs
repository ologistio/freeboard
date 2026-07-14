using System.Net;
using Freeboard.Auth;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Freeboard.Web.Tests;

/// <summary>
/// The shell view components rendered through real authenticated pages: the rail marks the active item
/// and gates the EE item by entitlement, no item badges (no count source), the breadcrumb renders linked
/// segments and degrades to a single segment for a group-less page, and the data-driven chrome (audit
/// countdown, notifications pip) renders empty because no backing source exists.
/// </summary>
public sealed class ShellChromeRenderTests
{
    private static HttpClient NoRedirectClient(AuthWebFactory factory)
        => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<string> GetHtmlAsync(AuthWebFactory factory, HttpClient client, string token, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", $"{SessionCookie.Name}={token}");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task RailMarksActiveItemAndOmitsBadges()
    {
        using var factory = new AuthWebFactory();
        var token = factory.SeedSession(AuthWebFactory.MakeUser("u1"));
        using var client = NoRedirectClient(factory);

        var html = await GetHtmlAsync(factory, client, token, "/home");

        // The Home item is active (aria-current="page") and no item carries a count badge.
        Assert.Contains("fb-navitem active", html, StringComparison.Ordinal);
        Assert.Contains("aria-current=\"page\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("fb-navcount", html, StringComparison.Ordinal);

        // Exactly one item is current within the rail. Scope the count to the rail region (it renders
        // before the topbar), so the breadcrumb terminal's own aria-current="page" is not counted here -
        // each landmark marks current independently, which is valid, but the rail must have exactly one.
        var rail = RailRegion(html);
        Assert.Equal(1, CountOccurrences(rail, "aria-current=\"page\""));
    }

    // The rail nav renders ahead of the topbar in the layout, so its markup is the slice up to the stage.
    private static string RailRegion(string html)
    {
        var start = html.IndexOf("class=\"fb-rail\"", StringComparison.Ordinal);
        var end = html.IndexOf("class=\"fb-stage\"", StringComparison.Ordinal);
        Assert.InRange(start, 0, end);
        return html[start..end];
    }

    private static string BreadcrumbRegion(string html)
    {
        var start = html.IndexOf("aria-label=\"Breadcrumb\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "breadcrumb nav is missing");
        var end = html.IndexOf("</nav>", start, StringComparison.Ordinal);
        return html[start..end];
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [Fact]
    public async Task RailGatesEnterpriseItemByEntitlement()
    {
        using (var off = new AuthWebFactory { CustomPoliciesEntitled = false })
        {
            var token = off.SeedSession(AuthWebFactory.MakeUser("admin1", role: "admin"));
            using var client = NoRedirectClient(off);
            var html = await GetHtmlAsync(off, client, token, "/home");
            Assert.DoesNotContain("/settings/custom-roles", html, StringComparison.Ordinal);
        }

        using var on = new AuthWebFactory { CustomPoliciesEntitled = true };
        var adminToken = on.SeedSession(AuthWebFactory.MakeUser("admin2", role: "admin"));
        using var onClient = NoRedirectClient(on);
        var onHtml = await GetHtmlAsync(on, onClient, adminToken, "/home");
        Assert.Contains("/settings/custom-roles", onHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BreadcrumbRendersLinkedSegmentsForAGroupedPage()
    {
        using var factory = new AuthWebFactory();
        var token = factory.SeedSession(AuthWebFactory.MakeUser("admin3", role: "admin"));
        using var client = NoRedirectClient(factory);

        var html = await GetHtmlAsync(factory, client, token, "/settings/users");

        // Group segment links to the Platform group's primary destination; the page segment names Users.
        Assert.Contains("aria-label=\"Breadcrumb\"", html, StringComparison.Ordinal);
        Assert.Contains("/settings/evidence-collectors", html, StringComparison.Ordinal);
        Assert.Contains("Platform", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AccountSubpageBreadcrumbReadsPlatformThenAccountThenLeaf()
    {
        using var factory = new AuthWebFactory();
        var token = factory.SeedSession(AuthWebFactory.MakeUser("u4"));
        using var client = NoRedirectClient(factory);

        var html = await GetHtmlAsync(factory, client, token, "/account/sessions");
        var crumb = BreadcrumbRegion(html);

        // Platform / Account / Active sessions: the Account parent segment links to /account, and the
        // leaf stays the page's own title carrying aria-current.
        Assert.Contains("href=\"/account\"", crumb, StringComparison.Ordinal);
        Assert.Contains(">Account</a>", crumb, StringComparison.Ordinal);
        Assert.Contains("aria-current=\"page\">Active sessions</a>", crumb, StringComparison.Ordinal);

        var platform = crumb.IndexOf("Platform", StringComparison.Ordinal);
        var account = crumb.IndexOf(">Account</a>", StringComparison.Ordinal);
        var leaf = crumb.IndexOf(">Active sessions</a>", StringComparison.Ordinal);
        Assert.True(platform >= 0 && platform < account && account < leaf, "breadcrumb order is group, Account, leaf");

        // The route move must not degrade the page's own <title>: it stays the leaf, not "Account".
        Assert.Contains("<title>Active sessions</title>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NestedAdminPageBreadcrumbReadsPlatformThenParentThenLeaf()
    {
        using var factory = new AuthWebFactory();
        var token = factory.SeedSession(AuthWebFactory.MakeUser("admin4", role: "admin"));
        using var client = NoRedirectClient(factory);

        var html = await GetHtmlAsync(factory, client, token, "/settings/usercredential");
        var crumb = BreadcrumbRegion(html);

        // Platform / Users / Temporary password: the Users parent segment links to /settings/users, and
        // the leaf stays the page's own title carrying aria-current.
        Assert.Contains("href=\"/settings/users\">Users</a>", crumb, StringComparison.Ordinal);
        Assert.Contains("aria-current=\"page\">Temporary password</a>", crumb, StringComparison.Ordinal);

        var platform = crumb.IndexOf("Platform", StringComparison.Ordinal);
        var parent = crumb.IndexOf(">Users</a>", StringComparison.Ordinal);
        var leaf = crumb.IndexOf(">Temporary password</a>", StringComparison.Ordinal);
        Assert.True(platform >= 0 && platform < parent && parent < leaf, "breadcrumb order is group, parent, leaf");
    }

    [Fact]
    public async Task BreadcrumbDegradesToASingleSegmentForAGroupLessPage()
    {
        using var factory = new AuthWebFactory();
        var token = factory.SeedSession(AuthWebFactory.MakeUser("u2"));
        using var client = NoRedirectClient(factory);

        var html = await GetHtmlAsync(factory, client, token, "/home");

        // Home is group-less: the breadcrumb is present with just the page segment, no Platform/Comply group.
        Assert.Contains("aria-label=\"Breadcrumb\"", html, StringComparison.Ordinal);
        Assert.Contains(">Home<", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CountdownAndPipRenderEmptyWithNoBackingData()
    {
        using var factory = new AuthWebFactory();
        var token = factory.SeedSession(AuthWebFactory.MakeUser("u3"));
        using var client = NoRedirectClient(factory);

        var html = await GetHtmlAsync(factory, client, token, "/home");

        // No audit-deadline or notification source exists, so neither the countdown pill nor the pip renders.
        Assert.DoesNotContain("fb-countdown", html, StringComparison.Ordinal);
        Assert.DoesNotContain("fb-pip", html, StringComparison.Ordinal);
    }
}
