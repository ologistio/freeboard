using System.Linq;
using System.Text.RegularExpressions;

namespace Freeboard.Web.Tests;

/// <summary>
/// Guards the theming and font wiring against the SERVED responses (not the gitignored build output
/// on disk): fonts are self-hosted, the pre-paint snippet is static and precedes the stylesheet,
/// dark applies only via an explicit data-theme override (no prefers-color-scheme rule), and every
/// token resolves in the default light :root.
/// </summary>
public sealed class ThemeAndFontHeadGuardTests
{
    private static readonly string AnonPage = "/login";

    [Fact]
    public async Task NoFontRequestTargetsAThirdPartyOrigin()
    {
        using var factory = new AuthWebFactory();
        using var client = factory.CreateClient();

        var css = await client.GetStringAsync("/css/app.css");
        var head = await client.GetStringAsync(AnonPage);

        Assert.DoesNotContain("googleapis", css, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gstatic", css, StringComparison.OrdinalIgnoreCase);

        // Every font url() in the served stylesheet is same-origin (app-relative).
        var fontUrls = Regex.Matches(css, @"url\(\s*([^)]+?)\s*\)")
            .Cast<Match>()
            .Select(m => m.Groups[1].Value.Trim('"', '\''))
            .Where(url => url.Contains(".woff", StringComparison.OrdinalIgnoreCase));
        foreach (var url in fontUrls)
            Assert.StartsWith("/fonts/", url);

        // Preloaded faces are served from the app too.
        var preloadedFonts = Regex.Matches(head, @"<link[^>]*rel=""preload""[^>]*>")
            .Cast<Match>()
            .Select(m => m.Value)
            .Where(link => link.Contains("as=\"font\"", StringComparison.Ordinal));
        foreach (var link in preloadedFonts)
            Assert.Contains("href=\"/fonts/", link, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrePaintSnippetIsStaticAndPrecedesTheStylesheet()
    {
        using var factory = new AuthWebFactory();
        using var client = factory.CreateClient();

        var a = await client.GetStringAsync(AnonPage + "?a=1");
        var b = await client.GetStringAsync(AnonPage + "?b=2");

        var snippetA = ThemeSnippet(a);
        var snippetB = ThemeSnippet(b);
        Assert.Equal(snippetA, snippetB); // byte-identical => no request-time interpolation => hash-allowlistable

        var scriptPos = a.IndexOf("fb-theme", StringComparison.Ordinal);
        var cssPos = a.IndexOf("css/app.css", StringComparison.Ordinal);
        Assert.True(scriptPos >= 0 && cssPos >= 0 && scriptPos < cssPos,
            "The theme snippet must precede the stylesheet link.");
    }

    [Fact]
    public async Task DarkIsAuthoredButActivatedOnlyByAnExplicitOverride()
    {
        using var factory = new AuthWebFactory();
        using var client = factory.CreateClient();

        var css = await client.GetStringAsync("/css/app.css");
        var head = await client.GetStringAsync(AnonPage);

        // The stylesheet emits no system-preference rule, so a dark system preference never applies.
        Assert.DoesNotContain("prefers-color-scheme", css, StringComparison.Ordinal);

        // Default (no override) is light: :root declares the light panel.
        var root = Regex.Match(css, @":root[^{]*\{(?<b>[^}]*)\}");
        Assert.True(root.Success && root.Groups["b"].Value.Contains("--color-panel:#fff", StringComparison.Ordinal),
            "Light panel must be the :root default.");

        // Both override sets exist and disagree, so the explicit switch reaches each theme.
        var dark = Block(css, "[data-theme=dark]");
        var light = Block(css, "[data-theme=light]");
        Assert.Contains("--color-panel:#1d2220", dark, StringComparison.Ordinal);
        Assert.Contains("--color-panel:#fff", light, StringComparison.Ordinal);

        // Server renders no data-theme attribute: the override is applied client-side, so a page
        // stays light until a person opts in to dark.
        Assert.DoesNotContain("data-theme=", head, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryThemeTokenResolvesInTheDefaultLightRoot()
    {
        using var factory = new AuthWebFactory();
        using var client = factory.CreateClient();

        var served = await client.GetStringAsync("/css/app.css");

        // Tailwind prunes unreferenced @theme custom properties from :root unless @theme static is
        // used. A page with no data-theme (system/absent = light) must be able to resolve every token
        // a utility, page, or component could reference, so assert each declared token is emitted in
        // the served default :root.
        var tokens = CssTokenSource.Tokens(CssTokenSource.Block(CssTokenSource.Read(), "@theme"));
        var root = ServedRoot(served);

        var missing = tokens.Keys.Where(k => !root.Contains(k + ":", StringComparison.Ordinal)).ToList();
        Assert.True(missing.Count == 0,
            "Tokens declared in @theme but absent from the default :root (add @theme static or reference them):\n"
            + string.Join("\n", missing));
    }

    [Fact]
    public void DarkAndLightOverridesCoverEveryThemeVaryingToken()
    {
        // Each override block must set every theme-varying token declared in @theme. Comparing the two
        // blocks only to each other would pass if a token were dropped from BOTH, letting dark and
        // light silently inherit the same @theme value. So the reference set is derived from @theme:
        // the colour and shadow tokens authored as literals. Aliases resolve via var()
        // (--color-brand-hover, --color-danger) and follow their canonical token, so they live only in
        // @theme and are excluded. Read the source, not the gitignored output.
        var css = CssTokenSource.Read();
        var themeVarying = CssTokenSource.Tokens(CssTokenSource.Block(css, "@theme"))
            .Where(kv => (kv.Key.StartsWith("--color-", StringComparison.Ordinal)
                    || kv.Key.StartsWith("--shadow", StringComparison.Ordinal))
                && !kv.Value.TrimStart().StartsWith("var(", StringComparison.Ordinal))
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.Ordinal);

        var dark = CssTokenSource.Tokens(CssTokenSource.Block(css, "html[data-theme=\"dark\"]")).Keys
            .ToHashSet(StringComparer.Ordinal);
        var light = CssTokenSource.Tokens(CssTokenSource.Block(css, "html[data-theme=\"light\"]")).Keys
            .ToHashSet(StringComparer.Ordinal);

        static string Fmt(IEnumerable<string> keys) => string.Join(", ", keys.OrderBy(k => k, StringComparer.Ordinal));
        var problems = new List<string>();
        if (themeVarying.Except(dark).Any()) problems.Add("Missing from dark: " + Fmt(themeVarying.Except(dark)));
        if (dark.Except(themeVarying).Any()) problems.Add("Extra in dark: " + Fmt(dark.Except(themeVarying)));
        if (themeVarying.Except(light).Any()) problems.Add("Missing from light: " + Fmt(themeVarying.Except(light)));
        if (light.Except(themeVarying).Any()) problems.Add("Extra in light: " + Fmt(light.Except(themeVarying)));

        Assert.True(problems.Count == 0,
            "Each theme override must set exactly the theme-varying tokens declared in @theme:\n"
            + string.Join("\n", problems));
    }

    // The :root rule that carries the design tokens (Tailwind emits :root,:host{...}).
    private static string ServedRoot(string css)
    {
        var body = Regex.Matches(css, @":root[^{]*\{(?<b>[^}]*)\}")
            .Cast<Match>()
            .Where(m => m.Groups["b"].Value.Contains("--color-brand:", StringComparison.Ordinal))
            .Select(m => m.Groups["b"].Value)
            .FirstOrDefault();
        return body ?? throw new Xunit.Sdk.XunitException("Token-bearing :root block not found in served CSS.");
    }

    private static string ThemeSnippet(string html)
    {
        var m = Regex.Match(html, @"<script>(?<s>[^<]*fb-theme[^<]*)</script>", RegexOptions.Singleline);
        Assert.True(m.Success, "Theme snippet not found in head.");
        return m.Groups["s"].Value;
    }

    private static string Block(string css, string selector)
    {
        var i = css.IndexOf(selector, StringComparison.Ordinal);
        Assert.True(i >= 0, $"Selector '{selector}' not found in served CSS.");
        var open = css.IndexOf('{', i);
        var close = css.IndexOf('}', open);
        return css[(open + 1)..close];
    }
}
