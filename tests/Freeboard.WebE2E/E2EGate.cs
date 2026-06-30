using Microsoft.Playwright;

namespace Freeboard.WebE2E;

/// <summary>
/// Gates the browser E2E suite so a plain <c>dotnet test</c> with no browser SKIPS cleanly (via
/// Xunit.SkippableFact) rather than failing. The gate requires BOTH:
/// <list type="bullet">
/// <item>the opt-in env var <c>FREEBOARD_TEST_E2E</c> is set (any non-empty value), and</item>
/// <item>a Chromium browser is actually launchable (installed with its OS dependencies).</item>
/// </list>
/// This mirrors the repo convention for the MySQL/SMTP integration tiers, which gate on a
/// connection-string env var. The env var alone is not enough: it also covers the case where the
/// var is set but the browser was never installed, so the tests still skip instead of erroring.
/// </summary>
internal static class E2EGate
{
    public const string EnvVar = "FREEBOARD_TEST_E2E";

    private static readonly Lazy<string?> Availability = new(Probe);

    /// <summary>Null when E2E can run; otherwise a human-readable skip reason.</summary>
    public static string? SkipReason => Availability.Value;

    public static bool CanRun => SkipReason is null;

    private static string? Probe()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnvVar)))
        {
            return $"Set {EnvVar} to run the browser E2E tests.";
        }

        try
        {
            using var playwright = Playwright.CreateAsync().GetAwaiter().GetResult();
            var browser = playwright.Chromium
                .LaunchAsync(new BrowserTypeLaunchOptions { Headless = true })
                .GetAwaiter().GetResult();
            browser.CloseAsync().GetAwaiter().GetResult();
            return null;
        }
        catch (PlaywrightException ex)
        {
            // No browser binary, missing OS deps, or a sandbox that blocks launch: skip, never fail.
            return $"Chromium is not launchable ({ex.GetType().Name}); install it with "
                + "`pwsh bin/Debug/net10.0/playwright.ps1 install --with-deps chromium`.";
        }
    }
}
