using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Freeboard.Persistence.Auth;
using Freeboard.TestInfrastructure;
using Freeboard.Web.Tests;
using Microsoft.Playwright;

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
        { "/home", Access.Full },
        { "/compliance/vendors", Access.Full },
        { "/settings/evidence-collectors", Access.Full },
        { "/settings/attestation-templates", Access.Full },
        { "/settings/integration-connections", Access.Full },
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
        { "/settings/users", Access.Admin },
        { "/settings/usercredential", Access.Admin },
        // The bare role-assignments page renders with no org loaded; the GET returns 200 without an
        // org guard, so an authenticated admin lands on the page itself, not a redirect.
        { "/settings/role-assignments", Access.Admin },
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

    /// <summary>
    /// Audits a layout page whose organisation selector is actually populated and expanded. The
    /// theory above seeds no organisations, so its selector renders only the "All Organisations" entry
    /// and its nested tree, selection marker, and toggle controls never appear. Here a multi-node tree
    /// plus a NESTED selection are seeded: selecting a child auto-unrolls its ancestor path, so the
    /// nested selected node and a toggle control are visible on load (axe skips invisible elements).
    /// Both are asserted present before auditing - so a selector that fails to render, or hides the
    /// selected node while collapsed, cannot pass this audit trivially.
    /// </summary>
    [RequiresEnvVarFact(EnvVar = E2EGate.EnvVar)]
    public async Task LayoutWithOrgSelector_HasNoAccessibilityViolations()
    {
        Gate();

        App.Compliance.Organisations =
        [
            new Freeboard.Persistence.OrganisationRow("org-a", "Org A", "Company", null),
            new Freeboard.Persistence.OrganisationRow("org-eng", "Engineering", "Department", "org-a"),
        ];

        await using var context = await NewContextAsync();
        var token = App.SeedSession(E2EAppFixture.MakeUser("a11y-orgsel"));
        await AddSessionCookieAsync(context, token);
        // Select the NESTED node: its ancestor path auto-expands, so the selected node is visible
        // without a manual toggle and axe can audit the nested tree markup and selection marker.
        await AddOrgSelectionCookieAsync(context, "org-eng");

        var page = await context.NewPageAsync();
        await page.GotoAsync($"{App.BaseUrl}/account");
        Assert.Equal("/account", new Uri(page.Url).AbsolutePath);

        // The selection's ancestor branch is expanded on load, so a toggle control and the nested
        // selected node are present and visible without interaction.
        var toggle = page.GetByRole(AriaRole.Button, new() { Name = "Toggle Org A" });
        Assert.True(await toggle.CountAsync() >= 1, "expected an expand/collapse toggle in the selector");

        var nested = page.GetByRole(AriaRole.Link, new() { Name = "Engineering" });
        await nested.First.WaitForAsync(new() { State = WaitForSelectorState.Visible });
        Assert.True(
            await nested.First.IsVisibleAsync(),
            "expected the selected nested tree node to be visible via ancestor auto-expansion");

        var result = await page.RunAxe(AccessibilityStandards);

        Assert.True(
            result.Violations.Length == 0,
            $"/account (with org selector): {result.Violations.Length} accessibility violation(s)\n"
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
