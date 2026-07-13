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
    private static string AnonPage = "/login";

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
        foreach (Match m in Regex.Matches(css, @"url\(\s*([^)]+?)\s*\)"))
        {
            var url = m.Groups[1].Value.Trim('"', '\'');
            if (url.Contains(".woff", StringComparison.OrdinalIgnoreCase))
                Assert.StartsWith("/fonts/", url);
        }

        // Preloaded faces are served from the app too.
        foreach (Match m in Regex.Matches(head, @"<link[^>]*rel=""preload""[^>]*>"))
        {
            if (m.Value.Contains("as=\"font\"", StringComparison.Ordinal))
                Assert.Contains("href=\"/fonts/", m.Value, StringComparison.Ordinal);
        }
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

    // The :root rule that carries the design tokens (Tailwind emits :root,:host{...}).
    private static string ServedRoot(string css)
    {
        foreach (Match m in Regex.Matches(css, @":root[^{]*\{(?<b>[^}]*)\}"))
            if (m.Groups["b"].Value.Contains("--color-brand:", StringComparison.Ordinal))
                return m.Groups["b"].Value;
        throw new Xunit.Sdk.XunitException("Token-bearing :root block not found in served CSS.");
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
