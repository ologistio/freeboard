using Deque.AxeCore.Playwright;
using Freeboard.Persistence;
using Freeboard.TestInfrastructure;
using Microsoft.Playwright;
using Xunit;

namespace Freeboard.WebE2E;

/// <summary>
/// Browser E2E for the object-detail drawer (O4/A5): the keyboard and focus behaviour the served-bundle
/// string markers cannot prove. Driven through Chromium over CDP; gated like the rest of the suite, so a
/// plain <c>dotnet test</c> with no browser skips cleanly. Covers open from a control, focus moving in,
/// the background staying inert while open (Tab cannot escape), the inert releasing on close, Escape and
/// scrim close with focus restored to the opener, that Ctrl-K cannot stack the palette over an open
/// drawer, reduced motion zeroing the slide, the no-JavaScript navigation fallback, and a clean axe audit
/// in both themes.
/// </summary>
[Trait("Category", TestCategories.E2E)]
public sealed class DrawerE2ETests : E2ETestBase
{
    private const string SoaUrl = "/compliance/statement-of-applicability?standard=std-a";

    private void SeedControl()
    {
        App.Compliance.Standards = [new StandardRow("std-a", "Standard A", "1.0", "Example Authority", null, null)];
        App.Compliance.Organisations = [new OrganisationRow("org-a", "Org A", "Company", null)];
        App.Compliance.Scopes = [new ScopeRow("scope-a", "Scope A", "org-a", "std-a", "In")];
        App.Compliance.Requirements =
        [
            new RequirementRow("req-a", "Requirement A", "std-a", "Theme", "Do the thing.", null, "L", "https://example.com/a"),
        ];
        App.Compliance.Controls = [new ControlRow("ctrl-a", "Control A", ["req-a"], "all")];
        App.Compliance.Collectors =
        [
            new EvidenceCollectorRow("coll-a", "Collector A", "ctrl-a", null, "integration", "daily", null, new Dictionary<string, string>()),
        ];
    }

    // Expands org -> requirement -> control so the control's drawer-opening anchor is visible to click.
    private static async Task RevealControlAsync(IPage page)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = "Toggle organisation org-a" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Toggle requirement req-a" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Toggle control ctrl-a" }).ClickAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Control A" })
            .WaitForAsync(new() { State = WaitForSelectorState.Visible });
    }

    private async Task<IPage> OpenDrawerAsync(IBrowserContext context, string id)
    {
        SeedControl();
        await SignInWithRecentSudoAsync(context, id);
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{App.BaseUrl}{SoaUrl}");
        await RevealControlAsync(page);
        await page.GetByRole(AriaRole.Link, new() { Name = "Control A" }).ClickAsync();
        await page.Locator(".fb-drawer").WaitForAsync(new() { State = WaitForSelectorState.Visible });
        return page;
    }

    private static Task<bool> IsOpenAsync(IPage page)
        => page.EvaluateAsync<bool>("() => document.querySelector('.fb-ddialog').classList.contains('is-open')");

    private static Task<bool> PaletteOpenAsync(IPage page)
        => page.EvaluateAsync<bool>("() => document.querySelector('.fb-pal').classList.contains('is-open')");

    [RequiresEnvVarFact(EnvVar = E2EGate.EnvVar)]
    public async Task OpensFromControl_MovesFocusIn_BackgroundInert_TabStaysInside()
    {
        Gate();
        await using var context = await NewContextAsync();
        var page = await OpenDrawerAsync(context, "drawer-open");

        Assert.True(await IsOpenAsync(page));
        // Focus moved into the labelled dialog panel.
        Assert.True(
            await page.EvaluateAsync<bool>(
                "() => { const p = document.querySelector('.fb-drawer'); return document.activeElement === p || p.contains(document.activeElement); }"),
            "focus should move into the dialog on open");

        // Both background siblings are inert, so neither keyboard nor pointer reaches the shell behind it.
        Assert.True(await page.EvaluateAsync<bool>("() => document.querySelector('.fb-rail').inert"));
        Assert.True(await page.EvaluateAsync<bool>("() => document.querySelector('.fb-stage').inert"));

        // Tab stays inside: an inert background has no tabbable descendant to escape to.
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.PressAsync("Tab");
        Assert.True(
            await page.EvaluateAsync<bool>("() => document.querySelector('.fb-ddialog').contains(document.activeElement)"),
            "Tab must not move focus out of the open drawer");
    }

    [RequiresEnvVarFact(EnvVar = E2EGate.EnvVar)]
    public async Task EscapeClosesReleasesInertAndRestoresFocusToOpener()
    {
        Gate();
        await using var context = await NewContextAsync();
        var page = await OpenDrawerAsync(context, "drawer-escape");

        await page.Keyboard.PressAsync("Escape");
        await page.Locator(".fb-drawer").WaitForAsync(new() { State = WaitForSelectorState.Hidden });

        Assert.False(await IsOpenAsync(page));
        // The inert this overlay applied is released on close.
        Assert.False(await page.EvaluateAsync<bool>("() => document.querySelector('.fb-rail').inert"));
        Assert.False(await page.EvaluateAsync<bool>("() => document.querySelector('.fb-stage').inert"));
        // Focus returns to the opener (the control anchor that opened the drawer).
        Assert.True(
            await page.EvaluateAsync<bool>(
                "() => !!document.activeElement && document.activeElement.matches('a[data-detail-template]')"),
            "Escape should restore focus to the control opener");
    }

    [RequiresEnvVarFact(EnvVar = E2EGate.EnvVar)]
    public async Task ScrimClickCloses()
    {
        Gate();
        await using var context = await NewContextAsync();
        var page = await OpenDrawerAsync(context, "drawer-scrim");

        await page.Locator(".fb-dscrim").ClickAsync(new() { Position = new() { X = 10, Y = 10 } });
        await page.Locator(".fb-drawer").WaitForAsync(new() { State = WaitForSelectorState.Hidden });
        Assert.False(await IsOpenAsync(page));
    }

    [RequiresEnvVarFact(EnvVar = E2EGate.EnvVar)]
    public async Task CtrlKDoesNotOpenThePaletteOverAnOpenDrawer()
    {
        Gate();
        await using var context = await NewContextAsync();
        var page = await OpenDrawerAsync(context, "drawer-ctrlk");

        await page.Keyboard.PressAsync("Control+k");
        // The one-overlay-at-a-time guard suppresses the palette; the drawer stays the top overlay.
        Assert.False(await PaletteOpenAsync(page), "Ctrl-K must not stack the palette over an open drawer");
        Assert.True(await IsOpenAsync(page), "the drawer must stay open");
    }

    [RequiresEnvVarFact(EnvVar = E2EGate.EnvVar)]
    public async Task ReducedMotionZeroesTheSlide()
    {
        Gate();
        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            ReducedMotion = ReducedMotion.Reduce,
        });
        var page = await OpenDrawerAsync(context, "drawer-reduced");

        // The global reduced-motion rule zeroes the panel's transition, so the slide is instant.
        var duration = await page.EvaluateAsync<string>(
            "() => getComputedStyle(document.querySelector('.fb-drawer')).transitionDuration");
        Assert.Contains("0.01ms", duration, StringComparison.Ordinal);
    }

    [RequiresEnvVarFact(EnvVar = E2EGate.EnvVar)]
    public async Task WithoutJavaScript_TheAnchorNavigatesToTheFullPage()
    {
        Gate();
        SeedControl();
        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            JavaScriptEnabled = false,
        });
        await SignInWithRecentSudoAsync(context, "drawer-nojs");
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{App.BaseUrl}{SoaUrl}");

        // No JS: the whole tree is server-rendered and the noscript reveal shows it, so the anchor is
        // clickable and, with no @click.prevent handler, navigates to the full-page detail (O4).
        await page.GetByRole(AriaRole.Link, new() { Name = "Control A" }).ClickAsync();
        await WaitForUrlContainingAsync(page, "/compliance/control-detail");
        Assert.Contains("Control A", await page.Locator("[data-control-detail]").InnerTextAsync(), StringComparison.Ordinal);
    }

    [RequiresEnvVarFact(EnvVar = E2EGate.EnvVar)]
    public async Task OpenDrawerPassesAxeInBothThemes()
    {
        Gate();

        // Light.
        await using (var light = await NewContextAsync())
        {
            var page = await OpenDrawerAsync(light, "drawer-axe-light");
            var result = await page.RunAxe();
            Assert.True(result.Violations.Length == 0, $"{result.Violations.Length} axe violation(s) on the open drawer (light)");
        }

        // Dark, seeded through the persisted theme the pre-paint reader consumes.
        await using var dark = await NewContextAsync();
        await dark.AddInitScriptAsync("try { localStorage.setItem('fb-theme', 'dark'); } catch (e) {}");
        var darkPage = await OpenDrawerAsync(dark, "drawer-axe-dark");
        Assert.Equal("dark", await darkPage.EvaluateAsync<string>("() => document.documentElement.dataset.theme"));
        var darkResult = await darkPage.RunAxe();
        Assert.True(darkResult.Violations.Length == 0, $"{darkResult.Violations.Length} axe violation(s) on the open drawer (dark)");
    }
}
