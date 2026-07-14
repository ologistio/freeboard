namespace Freeboard.Web.Tests;

/// <summary>
/// The theme toggle shipped in the served bundle honours the pre-paint reader's contract: it writes the
/// same <c>fb-theme</c> key and <c>light</c>/<c>dark</c> values the <c>_Head.cshtml</c> reader parses and
/// sets <c>data-theme</c> on the document element. The served stylesheet carries no
/// <c>prefers-color-scheme</c> activation, so a dark system preference never auto-applies dark (the
/// design-system dark-staging contract holds).
/// </summary>
public sealed class ShellThemeToggleTests
{
    [Fact]
    public async Task ThemeToggleWritesTheReadersKeyValuesAndAttribute()
    {
        using var factory = new AuthWebFactory();
        using var client = factory.CreateClient();

        var js = await client.GetStringAsync("/js/app.js");

        Assert.Contains("fb-theme", js, StringComparison.Ordinal);
        Assert.Contains("localStorage.setItem", js, StringComparison.Ordinal);
        // The setter drives the same document element attribute the pre-paint reader consumes.
        Assert.Contains("dataset.theme", js, StringComparison.Ordinal);
        Assert.Contains("\"light\"", js, StringComparison.Ordinal);
        Assert.Contains("\"dark\"", js, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServedCssHasNoPrefersColorSchemeActivation()
    {
        using var factory = new AuthWebFactory();
        using var client = factory.CreateClient();

        var css = await client.GetStringAsync("/css/app.css");

        Assert.DoesNotContain("prefers-color-scheme", css, StringComparison.OrdinalIgnoreCase);
    }
}
