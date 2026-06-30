using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Freeboard.TestInfrastructure;
using Freeboard.Web.Tests;

namespace Freeboard.WebE2E;

/// <summary>
/// Always-on accessibility baseline for the public auth pages. Unlike the browser tiers in this
/// project, it runs in a plain <c>dotnet test</c> with no Chromium: it serves the pages through the
/// in-memory factory and parses the rendered HTML. It asserts the static WCAG checks that do not need
/// a live DOM - language, page title, one top-level heading, a viewport, and a programmatic name for
/// every form control - so a page that drops a label or heading fails here, fast, on every run. The
/// in-browser axe audit (<see cref="AccessibilityAuditE2ETests"/>) covers the contrast/ARIA/focus
/// rules that need a real browser.
///
/// Tagged <c>NFR</c>: it asserts a non-functional (accessibility) requirement and runs in the NFR CI
/// tier, separate from the functional Unit tests.
/// </summary>
[Trait("Category", TestCategories.Nfr)]
public sealed class AccessibilityBaselineTests
{
    // Input types that carry no user-facing value and so need no label (WCAG 1.3.1/4.1.2 apply to
    // controls a user operates).
    private static readonly HashSet<string> UnlabelledInputTypes =
        new(StringComparer.OrdinalIgnoreCase) { "hidden", "submit", "button", "reset", "image" };

    [Theory]
    [InlineData("/login")]
    [InlineData("/forgot-password")]
    [InlineData("/reset-password")]
    public async Task PublicAuthPage_MeetsStaticAccessibilityBaseline(string path)
    {
        using var factory = new AuthWebFactory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync(path);
        var doc = new HtmlParser().ParseDocument(html);

        var lang = doc.QuerySelector("html")?.GetAttribute("lang");
        Assert.False(string.IsNullOrWhiteSpace(lang), $"{path}: <html> needs a non-empty lang (WCAG 3.1.1).");

        Assert.False(
            string.IsNullOrWhiteSpace(doc.Title), $"{path}: page needs a non-empty <title> (WCAG 2.4.2).");

        Assert.Single(doc.QuerySelectorAll("h1"));

        Assert.NotNull(doc.QuerySelector("meta[name=viewport]"));

        foreach (var control in doc.QuerySelectorAll("input, select, textarea"))
        {
            if (control.LocalName == "input"
                && UnlabelledInputTypes.Contains(control.GetAttribute("type") ?? "text"))
            {
                continue;
            }

            Assert.True(
                HasAccessibleName(doc, control),
                $"{path}: <{control.LocalName} id='{control.Id}'> has no programmatic label "
                    + "(<label for>, aria-label, aria-labelledby, or a wrapping <label>) (WCAG 1.3.1/4.1.2).");
        }
    }

    private static bool HasAccessibleName(IDocument doc, IElement control)
    {
        if (!string.IsNullOrWhiteSpace(control.GetAttribute("aria-label"))
            || !string.IsNullOrWhiteSpace(control.GetAttribute("aria-labelledby")))
        {
            return true;
        }

        if (control.Closest("label") is not null)
        {
            return true;
        }

        var id = control.Id;
        return !string.IsNullOrEmpty(id) && doc.QuerySelector($"label[for='{id}']") is not null;
    }
}
