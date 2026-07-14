using Freeboard.TestInfrastructure;
using Microsoft.Playwright;

namespace Freeboard.WebE2E;

/// <summary>
/// Browser E2E for the command palette (N7): the keyboard and focus behaviour that the served-bundle
/// string markers cannot prove. Driven through Chromium over CDP; gated like the rest of the suite, so a
/// plain <c>dotnet test</c> with no browser / no <c>FREEBOARD_TEST_E2E</c> skips cleanly. Covers open by
/// shortcut and by click, the "/" editable-target guard, combobox focus retention with active-descendant
/// tracking, Enter-to-navigate, Escape close with focus restore, background inertness, and that one
/// Escape closes only the palette when the mobile nav drawer is open behind it.
/// </summary>
[Trait("Category", TestCategories.E2E)]
public sealed class CommandPaletteE2ETests : E2ETestBase
{
    private async Task<IPage> SignedInPageAsync(IBrowserContext context, string id, string path = "/home")
    {
        var token = App.SeedSession(E2EAppFixture.MakeUser(id));
        await AddSessionCookieAsync(context, token);
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{App.BaseUrl}{path}");
        return page;
    }

    private static async Task WaitOpenAsync(IPage page)
        => await page.Locator(".fb-palinput").WaitForAsync(new() { State = WaitForSelectorState.Visible });

    private static Task<bool> IsOpenAsync(IPage page)
        => page.EvaluateAsync<bool>("() => document.querySelector('.fb-pal').classList.contains('is-open')");

    private static Task<bool> InputHasFocusAsync(IPage page)
        => page.EvaluateAsync<bool>(
            "() => document.activeElement && document.activeElement.classList.contains('fb-palinput')");

    private static Task<bool> AnyOptionHasFocusAsync(IPage page)
        => page.EvaluateAsync<bool>("() => !!document.querySelector('li[role=option]:focus')");

    [RequiresEnvVarFact(EnvVar = E2EGate.EnvVar)]
    public async Task OpensWithCtrlKAndByClick_FocusingTheInput()
    {
        Gate();
        await using var context = await NewContextAsync();
        var page = await SignedInPageAsync(context, "pal-open");

        await page.Keyboard.PressAsync("Control+k");
        await WaitOpenAsync(page);
        Assert.True(await InputHasFocusAsync(page), "Ctrl-K should open the palette and focus the input");

        await page.Keyboard.PressAsync("Escape");
        await page.Locator(".fb-palinput").WaitForAsync(new() { State = WaitForSelectorState.Hidden });

        await page.Locator(".fb-search-entry").ClickAsync();
        await WaitOpenAsync(page);
        Assert.True(await InputHasFocusAsync(page), "clicking the rail entry should open the palette and focus the input");
    }

    [RequiresEnvVarFact(EnvVar = E2EGate.EnvVar)]
    public async Task SlashOpensFromBody_ButIsTypedAsTextInAField()
    {
        Gate();
        await using var context = await NewContextAsync();
        var page = await SignedInPageAsync(context, "pal-slash");

        // From the page body, "/" opens the palette.
        await page.Locator("body").ClickAsync();
        await page.Keyboard.PressAsync("/");
        await WaitOpenAsync(page);
        Assert.True(await IsOpenAsync(page));
        await page.Keyboard.PressAsync("Escape");
        await page.Locator(".fb-palinput").WaitForAsync(new() { State = WaitForSelectorState.Hidden });

        // With a text field focused, "/" is ignored by the shortcut and entered as text instead.
        await page.EvaluateAsync(
            "() => { const i = document.createElement('input'); i.id = 'probe'; document.body.appendChild(i); i.focus(); }");
        await page.Keyboard.PressAsync("/");
        Assert.False(await IsOpenAsync(page), "\"/\" must not open the palette while a text field is focused");
        Assert.Equal("/", await page.Locator("#probe").InputValueAsync());
    }

    [RequiresEnvVarFact(EnvVar = E2EGate.EnvVar)]
    public async Task TypingFiltersAndArrowsMoveHighlight_WhileInputKeepsFocus()
    {
        Gate();
        await using var context = await NewContextAsync();
        var page = await SignedInPageAsync(context, "pal-filter");

        await page.Keyboard.PressAsync("Control+k");
        await WaitOpenAsync(page);

        // Typing filters; the first match becomes the active descendant.
        await page.Keyboard.TypeAsync("home");
        var input = page.Locator(".fb-palinput");
        var firstActive = await input.GetAttributeAsync("aria-activedescendant");
        Assert.False(string.IsNullOrEmpty(firstActive), "the first match should be the active descendant");
        Assert.Equal(
            firstActive,
            await page.EvaluateAsync<string>(
                "() => { const o = document.querySelector('li[role=option][aria-selected=\"true\"]'); return o ? o.id : ''; }"));

        // Arrows move the highlight while DOM focus stays on the input and no option ever holds focus.
        await page.Keyboard.PressAsync("ArrowDown");
        Assert.True(await InputHasFocusAsync(page), "the input must keep DOM focus while arrowing");
        Assert.False(await AnyOptionHasFocusAsync(page), "no option may hold DOM focus");
        await page.Keyboard.PressAsync("ArrowUp");
        Assert.True(await InputHasFocusAsync(page));
        Assert.False(await AnyOptionHasFocusAsync(page));
    }

    [RequiresEnvVarFact(EnvVar = E2EGate.EnvVar)]
    public async Task EnterOnPageOptionNavigates()
    {
        Gate();
        await using var context = await NewContextAsync();
        // Start off the destination so the navigation is observable.
        var page = await SignedInPageAsync(context, "pal-nav", "/account");

        await page.Keyboard.PressAsync("Control+k");
        await WaitOpenAsync(page);
        await page.Keyboard.TypeAsync("home");
        await page.Keyboard.PressAsync("Enter");

        await WaitForUrlAsync(page, $"{App.BaseUrl}/home");
        Assert.Equal("/home", new Uri(page.Url).AbsolutePath);
    }

    [RequiresEnvVarFact(EnvVar = E2EGate.EnvVar)]
    public async Task EscapeClosesAndRestoresFocusToOpener()
    {
        Gate();
        await using var context = await NewContextAsync();
        var page = await SignedInPageAsync(context, "pal-escape");

        await page.Keyboard.PressAsync("Control+k");
        await WaitOpenAsync(page);
        await page.Keyboard.PressAsync("Escape");
        await page.Locator(".fb-palinput").WaitForAsync(new() { State = WaitForSelectorState.Hidden });

        Assert.False(await IsOpenAsync(page));
        Assert.True(
            await page.EvaluateAsync<bool>(
                "() => document.activeElement && document.activeElement.classList.contains('fb-search-entry')"),
            "Escape should restore focus to the rail command-palette entry");
    }

    [RequiresEnvVarFact(EnvVar = E2EGate.EnvVar)]
    public async Task BackgroundIsInertWhileOpen()
    {
        Gate();
        await using var context = await NewContextAsync();
        var page = await SignedInPageAsync(context, "pal-inert");

        await page.Keyboard.PressAsync("Control+k");
        await WaitOpenAsync(page);

        // Both background siblings are inert, so neither keyboard focus nor a pointer can reach the rail,
        // topbar, or main behind the palette.
        Assert.True(await page.EvaluateAsync<bool>("() => document.querySelector('.fb-rail').inert"));
        Assert.True(await page.EvaluateAsync<bool>("() => document.querySelector('.fb-stage').inert"));

        // Tab stays inside the palette: an inert background has no tabbable descendants to escape to.
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.PressAsync("Tab");
        Assert.True(
            await page.EvaluateAsync<bool>("() => document.querySelector('.fb-pal').contains(document.activeElement)"),
            "Tab must not move focus out of the open palette");
    }

    [RequiresEnvVarFact(EnvVar = E2EGate.EnvVar)]
    public async Task EscapeClosesOnlyThePalette_OverTheOpenDrawer()
    {
        Gate();
        await using var context = await NewContextAsync();
        var page = await SignedInPageAsync(context, "pal-drawer");
        // Below the desktop breakpoint the rail is a drawer and its opener lives in the topbar.
        await page.SetViewportSizeAsync(800, 700);

        await page.GetByRole(AriaRole.Button, new() { Name = "Open navigation" }).ClickAsync();
        await page.WaitForFunctionAsync("() => document.querySelector('.fb-rail').classList.contains('is-open')");

        // Open the palette over the drawer, then press Escape once.
        await page.Keyboard.PressAsync("Control+k");
        await WaitOpenAsync(page);
        await page.Keyboard.PressAsync("Escape");
        await page.Locator(".fb-palinput").WaitForAsync(new() { State = WaitForSelectorState.Hidden });

        // Only the palette closed; the drawer stayed open and focus landed on the visible opener.
        Assert.False(await IsOpenAsync(page));
        Assert.True(
            await page.EvaluateAsync<bool>("() => document.querySelector('.fb-rail').classList.contains('is-open')"),
            "the mobile drawer must stay open when Escape closes the palette");
        Assert.True(
            await page.EvaluateAsync<bool>(
                "() => { const e = document.activeElement; return !!e && e.classList.contains('fb-search-entry') && e.offsetParent !== null; }"),
            "focus should land on the visible command-palette entry, never a hidden control");
    }

    [RequiresEnvVarFact(EnvVar = E2EGate.EnvVar)]
    public async Task EscapeRestoresFocusToAVisibleControl_WhenTheOpenerIsHidden()
    {
        Gate();
        await using var context = await NewContextAsync();
        var page = await SignedInPageAsync(context, "pal-hidden-opener");
        // Below the desktop breakpoint with the drawer CLOSED, the rail (and its .fb-search-entry opener)
        // is visibility:hidden. Opening by shortcut then closing must not restore focus to that hidden
        // entry or fall through to <body>; it lands on the visible topbar drawer toggle instead.
        await page.SetViewportSizeAsync(800, 700);
        // Let the rail settle to its hidden steady state (the resize runs a short visibility transition),
        // so the opener is genuinely hidden - the condition the fix must handle.
        await page.WaitForFunctionAsync(
            "() => getComputedStyle(document.querySelector('.fb-rail')).visibility === 'hidden'");

        await page.Keyboard.PressAsync("Control+k");
        await WaitOpenAsync(page);
        await page.Keyboard.PressAsync("Escape");
        await page.Locator(".fb-palinput").WaitForAsync(new() { State = WaitForSelectorState.Hidden });

        Assert.False(await IsOpenAsync(page));
        Assert.True(
            await page.EvaluateAsync<bool>(
                "() => { const e = document.activeElement; return !!e && e !== document.body && e.offsetParent !== null && getComputedStyle(e).visibility !== 'hidden'; }"),
            "focus should restore to a visible control, never <body> or a hidden opener");
        Assert.False(
            await page.EvaluateAsync<bool>(
                "() => !!document.activeElement && document.activeElement.classList.contains('fb-search-entry')"),
            "focus must not restore to the hidden rail entry");
    }

    [RequiresEnvVarFact(EnvVar = E2EGate.EnvVar)]
    public async Task ToggleThemeCommandThemesThePageAndSyncsTheTopbarToggle()
    {
        Gate();
        await using var context = await NewContextAsync();
        var page = await SignedInPageAsync(context, "pal-theme");

        // No seeded theme, so the app starts light and the topbar toggle reads not-pressed.
        var toggle = page.Locator("button[aria-pressed]");
        Assert.Equal("false", await toggle.GetAttributeAsync("aria-pressed"));

        await page.Keyboard.PressAsync("Control+k");
        await WaitOpenAsync(page);
        await page.Keyboard.TypeAsync("toggle dark");
        await page.Keyboard.PressAsync("Enter");
        await page.Locator(".fb-palinput").WaitForAsync(new() { State = WaitForSelectorState.Hidden });

        // The command themed the page through the shared source of truth...
        Assert.Equal("dark", await page.EvaluateAsync<string>("() => document.documentElement.dataset.theme"));
        Assert.Equal("dark", await page.EvaluateAsync<string>("() => localStorage.getItem('fb-theme')"));

        // ...and the topbar toggle reflects it: pressed, dark-icon shown, light-switch label.
        Assert.Equal("true", await toggle.GetAttributeAsync("aria-pressed"));
        Assert.Equal("Switch to light theme", await toggle.GetAttributeAsync("aria-label"));
        Assert.False(await toggle.Locator("svg").Nth(0).IsVisibleAsync(), "the sun icon must hide in dark");
        Assert.True(await toggle.Locator("svg").Nth(1).IsVisibleAsync(), "the moon icon must show in dark");
    }

    [RequiresEnvVarFact(EnvVar = E2EGate.EnvVar)]
    public async Task PageResultClosesThroughThePrimitive_RestoringFocus()
    {
        Gate();
        await using var context = await NewContextAsync();
        var page = await SignedInPageAsync(context, "pal-page-close");
        // Answer the outgoing navigation with 204 No Content: the browser keeps the current document (no
        // unload), so the same-route close/restore that the primitive performs before navigating is
        // observable on the live page.
        await page.RouteAsync("**/home", route => route.FulfillAsync(new() { Status = 204 }));

        await page.Keyboard.PressAsync("Control+k");
        await WaitOpenAsync(page);
        await page.Keyboard.TypeAsync("home");
        await page.Keyboard.PressAsync("Enter");

        // The Page result closed the palette through the primitive and restored focus to the opener.
        await page.Locator(".fb-palinput").WaitForAsync(new() { State = WaitForSelectorState.Hidden });
        Assert.False(await IsOpenAsync(page));
        await page.WaitForFunctionAsync(
            "() => { const e = document.activeElement; return !!e && e.classList.contains('fb-search-entry'); }");
    }

    [RequiresEnvVarFact(EnvVar = E2EGate.EnvVar)]
    public async Task ZeroMatchesLeavesTheComboboxFocusedWithNoActiveDescendant()
    {
        Gate();
        await using var context = await NewContextAsync();
        var page = await SignedInPageAsync(context, "pal-nomatch");

        await page.Keyboard.PressAsync("Control+k");
        await WaitOpenAsync(page);
        await page.Keyboard.TypeAsync("zzqxnomatch");

        // No option matches, so there is no active option: the attribute is absent (not empty string), and
        // the combobox input keeps focus.
        var input = page.Locator(".fb-palinput");
        Assert.Null(await input.GetAttributeAsync("aria-activedescendant"));
        Assert.True(await InputHasFocusAsync(page), "the combobox input must keep focus with zero matches");
    }
}
