using System.Net;
using Freeboard.Auth;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Freeboard.Web.Tests;

/// <summary>
/// The command palette rendered through real authenticated pages: it is an ARIA combobox over a listbox
/// with the required attributes, its opener advertises the dialog, its Page options come from the same
/// gated nav catalog as the rail (so a gated destination absent from the rail is absent from the palette
/// too), it carries the Toggle dark mode Command, and it shows no Agent option. The served bundle and
/// stylesheet carry the palette and focus-overlay markers with no prefers-color-scheme activation.
/// </summary>
public sealed class ShellPaletteRenderTests
{
    private static HttpClient NoRedirectClient(AuthWebFactory factory)
        => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<string> GetHtmlAsync(HttpClient client, string token, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", $"{SessionCookie.Name}={token}");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    // The palette listbox region: the options live between the listbox open and its close.
    private static string PaletteListRegion(string html)
    {
        var start = html.IndexOf("id=\"fb-pal-list\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "palette listbox is missing");
        var end = html.IndexOf("</ul>", start, StringComparison.Ordinal);
        return html[start..end];
    }

    [Fact]
    public async Task PaletteRendersComboboxListboxAndOpenerAdvertisesDialog()
    {
        using var factory = new AuthWebFactory();
        var token = factory.SeedSession(AuthWebFactory.MakeUser("u1"));
        using var client = NoRedirectClient(factory);

        var html = await GetHtmlAsync(client, token, "/home");

        // The combobox input and its required ARIA.
        Assert.Contains("role=\"combobox\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-expanded=\"true\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-controls=\"fb-pal-list\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-autocomplete=\"list\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-activedescendant", html, StringComparison.Ordinal);

        // The listbox and its option rows.
        Assert.Contains("id=\"fb-pal-list\"", html, StringComparison.Ordinal);
        Assert.Contains("role=\"listbox\"", html, StringComparison.Ordinal);
        Assert.Contains("role=\"option\"", html, StringComparison.Ordinal);

        // The opener advertises the dialog it opens.
        Assert.Contains("aria-haspopup=\"dialog\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PalettePageOptionsComeFromTheCatalog()
    {
        using var factory = new AuthWebFactory();
        var token = factory.SeedSession(AuthWebFactory.MakeUser("u2"));
        using var client = NoRedirectClient(factory);

        var html = await GetHtmlAsync(client, token, "/home");
        var list = PaletteListRegion(html);

        // A reachable nav destination is a Page option with its route as the target.
        Assert.Contains("data-kind=\"page\"", list, StringComparison.Ordinal);
        Assert.Contains("data-route=\"/home\"", list, StringComparison.Ordinal);
        Assert.Contains(">Page<", list, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PaletteHasToggleDarkModeCommandAndNoAgentOption()
    {
        using var factory = new AuthWebFactory();
        var token = factory.SeedSession(AuthWebFactory.MakeUser("u3"));
        using var client = NoRedirectClient(factory);

        var html = await GetHtmlAsync(client, token, "/home");
        var list = PaletteListRegion(html);

        // The one Command result is present and tagged.
        Assert.Contains("data-command=\"toggle-theme\"", list, StringComparison.Ordinal);
        Assert.Contains("Toggle dark mode", list, StringComparison.Ordinal);
        Assert.Contains(">Command<", list, StringComparison.Ordinal);

        // No Agent-tagged result of any kind (not even a placeholder or coming-soon row).
        Assert.DoesNotContain("data-kind=\"agent\"", list, StringComparison.Ordinal);
        Assert.DoesNotContain(">Agent<", list, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PaletteGatesEnterpriseDestinationByEntitlement()
    {
        // A gated destination absent from the rail is absent from the palette too (both resolve the one
        // catalog through ShellNavResolver). Mirrors RailGatesEnterpriseItemByEntitlement.
        using (var off = new AuthWebFactory { CustomPoliciesEntitled = false })
        {
            var token = off.SeedSession(AuthWebFactory.MakeUser("admin1", role: "admin"));
            using var client = NoRedirectClient(off);
            var list = PaletteListRegion(await GetHtmlAsync(client, token, "/home"));
            Assert.DoesNotContain("/settings/custom-roles", list, StringComparison.Ordinal);
        }

        using var on = new AuthWebFactory { CustomPoliciesEntitled = true };
        var adminToken = on.SeedSession(AuthWebFactory.MakeUser("admin2", role: "admin"));
        using var onClient = NoRedirectClient(on);
        var onList = PaletteListRegion(await GetHtmlAsync(onClient, adminToken, "/home"));
        Assert.Contains("/settings/custom-roles", onList, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PaletteGatesAdminDestinationByAuthorization()
    {
        // /settings/users is gated by authorization (CanReachAdmin), not entitlement. A member who lacks
        // that permission sees it absent from the palette, exactly as the rail gates it; an admin sees it.
        using var factory = new AuthWebFactory();

        var memberToken = factory.SeedSession(AuthWebFactory.MakeUser("member1"));
        using var memberClient = NoRedirectClient(factory);
        var memberList = PaletteListRegion(await GetHtmlAsync(memberClient, memberToken, "/home"));
        Assert.DoesNotContain("/settings/users", memberList, StringComparison.Ordinal);

        var adminToken = factory.SeedSession(AuthWebFactory.MakeUser("admin-users", role: "admin"));
        using var adminClient = NoRedirectClient(factory);
        var adminList = PaletteListRegion(await GetHtmlAsync(adminClient, adminToken, "/home"));
        Assert.Contains("/settings/users", adminList, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServedBundleCarriesPaletteAndFocusOverlayMarkers()
    {
        using var factory = new AuthWebFactory();
        using var client = factory.CreateClient();

        var js = await client.GetStringAsync("/js/app.js");

        // The palette component and its command.
        Assert.Contains("commandPalette", js, StringComparison.Ordinal);
        Assert.Contains("toggle-theme", js, StringComparison.Ordinal);
        // Open-shortcut handling.
        Assert.Contains("metaKey", js, StringComparison.Ordinal);
        // The focus-overlay inerts both background siblings and restores focus to the rail opener.
        Assert.Contains(".fb-rail", js, StringComparison.Ordinal);
        Assert.Contains(".fb-stage", js, StringComparison.Ordinal);
        Assert.Contains("fb-search-entry", js, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServedStylesheetCarriesPaletteClassesAndNoPrefersColorScheme()
    {
        using var factory = new AuthWebFactory();
        using var client = factory.CreateClient();

        var css = await client.GetStringAsync("/css/app.css");

        Assert.Contains(".fb-pal", css, StringComparison.Ordinal);
        Assert.Contains(".fb-palbox", css, StringComparison.Ordinal);
        Assert.Contains(".fb-palinput", css, StringComparison.Ordinal);
        Assert.Contains(".fb-pallist", css, StringComparison.Ordinal);
        Assert.Contains(".fb-palfoot", css, StringComparison.Ordinal);
        Assert.DoesNotContain("prefers-color-scheme", css, StringComparison.OrdinalIgnoreCase);
    }
}
