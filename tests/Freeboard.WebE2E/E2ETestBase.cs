using Microsoft.Playwright;
using Xunit;

namespace Freeboard.WebE2E;

/// <summary>
/// Base for browser E2E tests. Every test first calls <see cref="Gate"/>, which SKIPS (never fails)
/// when the browser/env is absent. Only when the gate passes does it boot the HTTPS Kestrel app and
/// launch Chromium, so the expensive setup is paid solely on a real E2E run. The browser ignores
/// HTTPS errors because the dev cert is self-signed for localhost.
/// </summary>
public abstract class E2ETestBase : IAsyncLifetime
{
    private E2EAppFixture? _app;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    private protected E2EAppFixture App => _app!;

    private protected IBrowser Browser => _browser!;

    /// <summary>Skips the test cleanly when the browser/env is unavailable. Call first in every test.</summary>
    private protected static void Gate() => Skip.IfNot(E2EGate.CanRun, E2EGate.SkipReason);

    public async Task InitializeAsync()
    {
        if (!E2EGate.CanRun)
        {
            // Do not boot the app or a browser when the suite is gated off; the tests will skip.
            return;
        }

        // Register the real email sender so the forgot/reset round-trip records the reset link.
        _app = new E2EAppFixture { RegisterEmailSender = true };
        // Start the single Kestrel-hosted app so the socket is listening and its DI (the same fakes
        // the seeding helpers write to) is live before the browser navigates.
        _app.EnsureStarted();

        _playwright = await Playwright.CreateAsync();
        // Default headless (CI). Set FREEBOARD_E2E_HEADED to watch the browser drive the flows live;
        // FREEBOARD_E2E_SLOWMO (milliseconds) adds a delay between actions so they are followable.
        var headed = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FREEBOARD_E2E_HEADED"));
        float.TryParse(Environment.GetEnvironmentVariable("FREEBOARD_E2E_SLOWMO"), out var slowMo);
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = !headed,
            SlowMo = headed && slowMo > 0 ? slowMo : (headed ? 600 : 0),
        });
    }

    public async Task DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.DisposeAsync();
        }

        _playwright?.Dispose();
        _app?.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>A browser context that ignores the self-signed localhost cert's HTTPS errors.</summary>
    private protected Task<IBrowserContext> NewContextAsync()
        => Browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });

    /// <summary>
    /// Waits until the browser has navigated to <paramref name="url"/> (query string ignored) by polling
    /// the live document location, not Playwright's <c>WaitForURLAsync</c>. Under scheduler delay a fast
    /// navigation can fire its <c>load</c> event before a <c>WaitForURLAsync</c> wait attaches; that wait
    /// then blocks on an event that already fired and hangs until timeout. Polling <c>location.href</c> is
    /// immune to the missed event.
    /// </summary>
    private protected static Task WaitForUrlAsync(IPage page, string url, float timeoutMs = 15000)
        => page.WaitForFunctionAsync(
            "u => location.href.split('?')[0] === u", url, new() { Timeout = timeoutMs });

    /// <summary>
    /// Like <see cref="WaitForUrlAsync"/>, but settles when the live location merely contains
    /// <paramref name="fragment"/>. Used where the destination is identified by a path substring rather
    /// than an exact URL.
    /// </summary>
    private protected static Task WaitForUrlContainingAsync(IPage page, string fragment, float timeoutMs = 15000)
        => page.WaitForFunctionAsync(
            "f => location.href.includes(f)", fragment, new() { Timeout = timeoutMs });

    /// <summary>Seeds a user with a password credential (no session) so a browser can log in.</summary>
    private protected void SeedUserWithPassword(
        string id, string password, bool forceReset = false, bool mfaEnabled = false)
    {
        var user = E2EAppFixture.MakeUser(id, forcePasswordReset: forceReset, mfaEnabled: mfaEnabled);
        App.Users.Add(user);
        App.Credentials.SetAsync(user.Id, App.Hasher.Hash(password), 1).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Seeds a user plus a Full session whose <c>sudo_at</c> is fresh, and sets it as the
    /// <c>__Host-</c> session cookie so a sudo-gated page (passkey register/remove) is reachable
    /// without driving the whole login + step-up UI first. The cookie is host-only (no Domain) and
    /// Secure, which is the only shape a browser accepts for a <c>__Host-</c> cookie.
    /// </summary>
    private protected async Task<string> SignInWithRecentSudoAsync(IBrowserContext context, string id)
    {
        var user = E2EAppFixture.MakeUser(id);
        var token = App.SeedSessionWithSudo(user);
        await AddSessionCookieAsync(context, token);
        return token;
    }

    /// <summary>The session id <see cref="SeedSessionWithSudo"/> creates for a user.</summary>
    private protected static string SudoSessionId(string id) => $"sudo-{id}";

    /// <summary>
    /// Sets the opaque session token as the <c>__Host-freeboard-session</c> cookie. A <c>__Host-</c>
    /// cookie MUST be host-only (no Domain), Secure, and Path=/. Passing only <c>Url</c> (Playwright
    /// rejects Url + Path together) scopes the cookie to the app host with no Domain attribute and
    /// defaults the path to /.
    /// </summary>
    private protected async Task AddSessionCookieAsync(IBrowserContext context, string token)
        => await context.AddCookiesAsync([
            new Cookie
            {
                Name = "__Host-freeboard-session",
                Value = token,
                Url = App.BaseUrl,
                Secure = true,
                HttpOnly = true,
                SameSite = SameSiteAttribute.Strict,
            }
        ]);

    /// <summary>
    /// Sets the <c>freeboard-org</c> selection cookie (HttpOnly, Secure, SameSite=Lax) so a page loads
    /// with an organisation already selected, without driving the selector UI first.
    /// </summary>
    private protected async Task AddOrgSelectionCookieAsync(IBrowserContext context, string organisationId)
        => await context.AddCookiesAsync([
            new Cookie
            {
                Name = "freeboard-org",
                Value = organisationId,
                Url = App.BaseUrl,
                Secure = true,
                HttpOnly = true,
                SameSite = SameSiteAttribute.Lax,
            }
        ]);
}
