using System.Net;
using Freeboard.Auth;
using Freeboard.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Freeboard.Web.Tests;

/// <summary>
/// The object-detail drawer rendered through real authenticated pages: it is one shell-mounted ARIA
/// dialog that is inert while closed, the Statement of Applicability control rows advertise it and carry
/// an adjacent server-rendered anatomy template, and the served bundle/stylesheet carry the drawer and
/// store markers with no prefers-color-scheme activation.
/// </summary>
public sealed class ObjectDrawerRenderTests
{
    private const string SoaPath = "/compliance/statement-of-applicability";

    private static HttpClient NoRedirectClient(AuthWebFactory factory)
        => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<string> GetHtmlAsync(AuthWebFactory factory, HttpClient client, string path)
    {
        var token = factory.SeedSession(AuthWebFactory.MakeUser("drawer1"));
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", $"{SessionCookie.Name}={token}");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    // org-a is In (default). ctrl-a maps to req-a with one collector check; the collector is seeded Passing.
    private static FakeComplianceStore DrilldownStore() => new()
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

    [Fact]
    public async Task DrawerDialogIsMountedWithAriaAndInertWhenClosed()
    {
        using var factory = new AuthWebFactory();
        using var client = NoRedirectClient(factory);

        var html = await GetHtmlAsync(factory, client, "/home");

        Assert.Contains("class=\"fb-ddialog\"", html, StringComparison.Ordinal);
        Assert.Contains("role=\"dialog\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-modal=\"true\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-labelledby=\"fb-detail-title\"", html, StringComparison.Ordinal);
        // Closed: the panel is bound inert while the drawer store is not open, so its controls stay out of
        // the tab order and the accessibility tree until it opens.
        Assert.Contains(":inert=\"!$store.drawer.open || null\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SoaControlIsAnAnchorAdvertisingTheDialogWithAnAdjacentTemplate()
    {
        using var factory = new AuthWebFactory { Compliance = DrilldownStore() };
        using var client = NoRedirectClient(factory);

        var html = await GetHtmlAsync(factory, client, $"{SoaPath}?standard=std-a");

        // The control object is an anchor to its full-page detail URL (the no-JS fallback), advertises the
        // dialog, and points at its adjacent template; the existing control marker is preserved.
        Assert.Contains("data-control-id=\"ctrl-a\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-haspopup=\"dialog\"", html, StringComparison.Ordinal);
        Assert.Contains("/compliance/control-detail?standard=std-a&amp;org=org-a&amp;requirement=req-a&amp;control=ctrl-a", html, StringComparison.Ordinal);
        Assert.Contains("data-detail-template=\"fb-detail-tmpl-0\"", html, StringComparison.Ordinal);
        // The anatomy is server-rendered into an adjacent inert template, not fetched.
        Assert.Contains("<template id=\"fb-detail-tmpl-0\">", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServedBundleCarriesDrawerAndStoreMarkers()
    {
        using var factory = new AuthWebFactory();
        using var client = factory.CreateClient();

        var js = await client.GetStringAsync("/js/app.js");

        Assert.Contains("objectDrawer", js, StringComparison.Ordinal);
        Assert.Contains("store(\"drawer\"", js, StringComparison.Ordinal);
        Assert.Contains("detailTemplate", js, StringComparison.Ordinal);
        // The drawer reuses the focus-overlay's background inert on the same two shell siblings.
        Assert.Contains(".fb-rail", js, StringComparison.Ordinal);
        Assert.Contains(".fb-stage", js, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServedStylesheetCarriesDrawerClassesAndNoPrefersColorScheme()
    {
        using var factory = new AuthWebFactory();
        using var client = factory.CreateClient();

        var css = await client.GetStringAsync("/css/app.css");

        Assert.Contains(".fb-ddialog", css, StringComparison.Ordinal);
        Assert.Contains(".fb-dscrim", css, StringComparison.Ordinal);
        Assert.Contains(".fb-drawer", css, StringComparison.Ordinal);
        Assert.Contains(".fb-sheet", css, StringComparison.Ordinal);
        Assert.DoesNotContain("prefers-color-scheme", css, StringComparison.OrdinalIgnoreCase);
    }
}
