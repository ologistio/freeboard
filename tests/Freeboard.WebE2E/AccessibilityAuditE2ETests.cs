using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Freeboard.Persistence.Auth;
using Freeboard.TestInfrastructure;
using Freeboard.Web.Tests;

namespace Freeboard.WebE2E;

/// <summary>
/// Accessibility audit for every rendered web view - public auth pages, the authenticated account and
/// MFA pages, the sudo-gated enrolment pages, and the admin pages. Each page is loaded in Chromium in
/// the session state that renders its real UI (seeded the same way the other E2E tests seed sessions)
/// and audited with axe-core for the WCAG 2.0/2.1 level A and AA success criteria, asserting zero
/// violations. Gated like the rest of the suite: with no browser / no <c>FREEBOARD_TEST_E2E</c> these
/// skip cleanly.
///
/// Pages that render no standalone view are out of scope: the GET-redirect POST endpoints
/// (<c>/logout</c>, <c>/account/sessions/revoke</c>, the MFA <c>*/remove</c> and recovery-regenerate
/// handlers) and the magic-link consumer (<c>/auth/magic-link</c>), which only redirect.
/// </summary>
[Trait("Category", TestCategories.E2E)]
public sealed class AccessibilityAuditE2ETests : E2ETestBase
{
    // axe rule tags for WCAG 2.0 and 2.1, level A and AA - the conformance target most teams hold to.
    private static readonly AxeRunOptions WcagAaOptions = new()
    {
        RunOnly = new RunOnlyOptions
        {
            Type = "tag",
            Values = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"],
        },
    };

    /// <summary>The session state a page needs before it renders its real view.</summary>
    public enum Access
    {
        Anonymous,
        ResetToken,
        Full,
        ForceReset,
        Sudo,
        Admin,
    }

    public static TheoryData<string, Access> Pages() => new()
    {
        // Public, no session.
        { "/login", Access.Anonymous },
        { "/forgot-password", Access.Anonymous },
        { "/setup", Access.Anonymous },
        { "/login/mfa/totp", Access.Anonymous },
        { "/login/mfa/recovery", Access.Anonymous },
        { "/login/mfa/magic-link", Access.Anonymous },
        { "/login/mfa/passkey", Access.Anonymous },
        // The reset form renders only once the transient reset-token cookie is set; the `?token=`
        // GET stashes it and 302s to the bare path, so navigating with the query lands on the form.
        { "/reset-password?token=a11y", Access.ResetToken },
        // Authenticated (full session).
        { "/account", Access.Full },
        { "/account/mfa", Access.Full },
        { "/account/password/change", Access.Full },
        { "/account/sessions", Access.Full },
        { "/account/sudo", Access.Full },
        // Force-reset-limited session is funnelled to the completion page.
        { "/account/complete-reset", Access.ForceReset },
        // Sudo-gated enrolment pages need a fresh-sudo session.
        { "/account/mfa/totp", Access.Sudo },
        { "/account/mfa/passkey", Access.Sudo },
        // Admin role.
        { "/admin/users", Access.Admin },
        { "/admin/usercredential", Access.Admin },
    };

    [RequiresEnvVarTheory(EnvVar = E2EGate.EnvVar)]
    [MemberData(nameof(Pages))]
    public async Task Page_HasNoWcagAaViolations(string path, Access access)
    {
        Gate();

        await using var context = await NewContextAsync();
        if (SeedSessionToken(access) is { } token)
        {
            await AddSessionCookieAsync(context, token);
        }

        var page = await context.NewPageAsync();
        await page.GotoAsync($"{App.BaseUrl}{path}");

        // Confirm the seeded state actually rendered the intended page. A redirect to /login (or the
        // sudo/complete-reset funnel) would mean the state seeding is wrong and axe would audit the
        // wrong page - exactly the false pass we must avoid.
        var expectedPath = path.Split('?')[0];
        Assert.Equal(expectedPath, new Uri(page.Url).AbsolutePath);

        var result = await page.RunAxe(WcagAaOptions);

        Assert.True(
            result.Violations.Length == 0,
            $"{expectedPath} has axe WCAG A/AA violations:\n"
                + string.Join("\n", result.Violations.Select(v => $"  - {v.Id} ({v.Impact}): {v.Help}")));
    }

    /// <summary>
    /// Seeds the session the page needs and returns its cookie token, or null when the page is reached
    /// anonymously. Mirrors the seeding the other E2E tests use.
    /// </summary>
    private string? SeedSessionToken(Access access) => access switch
    {
        Access.Anonymous or Access.ResetToken => null,
        Access.Full => App.SeedSession(E2EAppFixture.MakeUser("a11y-full")),
        Access.ForceReset => App.SeedSession(
            E2EAppFixture.MakeUser("a11y-freset", forcePasswordReset: true), SessionAuthState.ForceResetLimited),
        Access.Sudo => App.SeedSessionWithSudo(E2EAppFixture.MakeUser("a11y-sudo")),
        Access.Admin => App.SeedSession(E2EAppFixture.MakeUser("a11y-admin", role: "admin")),
        _ => null,
    };
}
