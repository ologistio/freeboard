using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Freeboard.TestInfrastructure;

namespace Freeboard.WebE2E;

/// <summary>
/// In-browser accessibility audit: loads each public auth page in Chromium and runs the axe-core
/// engine against the live DOM, asserting no WCAG 2.0/2.1 A or AA violations. This is the high-fidelity
/// tier - it catches the rules a static HTML parse cannot (colour contrast, ARIA wiring, focus order).
/// It needs a real browser, so it is gated like the rest of the suite and SKIPS cleanly without one;
/// the always-on static checks live in <see cref="AccessibilityBaselineTests"/>.
/// </summary>
[Trait("Category", TestCategories.E2E)]
public sealed class AccessibilityAuditE2ETests : E2ETestBase
{
    // axe rule tags for the WCAG 2.0 and 2.1, level A and AA, success criteria - the conformance
    // target most teams hold themselves to.
    private static readonly AxeRunOptions WcagAaOptions = new()
    {
        RunOnly = new RunOnlyOptions
        {
            Type = "tag",
            Values = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"],
        },
    };

    [RequiresEnvVarTheory(EnvVar = E2EGate.EnvVar)]
    [InlineData("/login")]
    [InlineData("/forgot-password")]
    [InlineData("/reset-password")]
    public async Task PublicAuthPage_HasNoWcagAaViolations(string path)
    {
        Gate();

        await using var context = await NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{App.BaseUrl}{path}");
        // The heading is the last thing each auth page renders; waiting on it means axe sees the
        // settled DOM, not a half-built one.
        await page.WaitForSelectorAsync("h1");

        var result = await page.RunAxe(WcagAaOptions);

        Assert.True(
            result.Violations.Length == 0,
            $"{path} has axe WCAG A/AA violations:\n"
                + string.Join("\n", result.Violations.Select(v => $"  - {v.Id} ({v.Impact}): {v.Help}")));
    }
}
