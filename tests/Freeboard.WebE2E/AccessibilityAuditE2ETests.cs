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
/// and audited with axe-core against every accessibility standard it supports - WCAG 2.0/2.1/2.2 at
/// all levels including AAA, Section 508, and EN 301 549 - asserting zero violations. Gated like the
/// rest of the suite: with no browser / no <c>FREEBOARD_TEST_E2E</c> these skip cleanly.
///
/// Pages that render no standalone view are out of scope: the GET-redirect POST endpoints
/// (<c>/logout</c>, <c>/account/sessions/revoke</c>, the MFA <c>*/remove</c> and recovery-regenerate
/// handlers) and the magic-link consumer (<c>/auth/magic-link</c>), which only redirect.
/// </summary>
[Trait("Category", TestCategories.E2E)]
public sealed class AccessibilityAuditE2ETests : E2ETestBase
{
    // Every accessibility standard the axe engine supports: WCAG 2.0/2.1/2.2 at all levels including
    // AAA, plus Section 508, the EU EN 301 549 standard, and axe's best-practice rules. Only the
    // experimental rules are left out - they are explicitly unstable and would make the suite fragile
    // across axe versions. This is a deliberately maximal bar, matching a total commitment to access.
    private static readonly AxeRunOptions AccessibilityStandards = new()
    {
        RunOnly = new RunOnlyOptions
        {
            Type = "tag",
            Values =
            [
                "wcag2a", "wcag2aa", "wcag2aaa",
                "wcag21a", "wcag21aa",
                "wcag22aa",
                "section508",
                "EN-301-549",
                "best-practice",
            ],
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
    public async Task Page_HasNoAccessibilityViolations(string path, Access access)
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

        var result = await page.RunAxe(AccessibilityStandards);

        Assert.True(
            result.Violations.Length == 0,
            $"{expectedPath}: {result.Violations.Length} accessibility violation(s)\n"
                + string.Join("\n", result.Violations.Select(DescribeViolation)));
    }

    /// <summary>Renders one axe violation as the rule, its impact and help, the docs URL, and each
    /// failing node's CSS selector and HTML - enough for a developer or agent to locate and fix it.</summary>
    private static string DescribeViolation(AxeResultItem v)
        => $"  [{v.Impact}] {v.Id}: {v.Help}\n"
            + $"    help: {v.HelpUrl}\n"
            + string.Join("\n", v.Nodes.Select(n => $"    at {n.Target}\n      {n.Html}"));

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
