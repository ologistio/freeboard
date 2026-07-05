using Freeboard.Persistence;
using Freeboard.TestInfrastructure;
using Freeboard.Web.Tests;
using Microsoft.Playwright;
using Xunit;

namespace Freeboard.WebE2E;

/// <summary>
/// Browser E2E proving per-org read isolation: two users granted on different org subtrees each see
/// only their own subtree in the Statement of Applicability. Runs in the default Compat mode, where a
/// grant-holder is narrowed to its authorized subtree (a zero-grant caller would keep the full
/// fallback). Gated: a plain <c>dotnet test</c> with no browser skips.
/// </summary>
[Trait("Category", TestCategories.E2E)]
public sealed class AuthzIsolationE2ETests : E2ETestBase
{
    [RequiresEnvVarFact(EnvVar = E2EGate.EnvVar)]
    public async Task TwoUsersOnDifferentSubtrees_EachSeeOnlyTheirOwn()
    {
        Gate();

        App.Compliance.Standards = [new StandardRow("std-a", "Standard A", "1.0", "Example Authority", null, null)];
        App.Compliance.Organisations =
        [
            new OrganisationRow("org-a", "Org A", "Company", null),
            new OrganisationRow("org-eng", "Engineering", "Department", "org-a"),
            new OrganisationRow("org-b", "Org B", "Company", null),
        ];
        App.Compliance.Scopes =
        [
            new ScopeRow("scope-a", "Scope A", "org-a", "std-a", "In"),
            new ScopeRow("scope-b", "Scope B", "org-b", "std-a", "In"),
        ];

        var soaUrl = $"{App.BaseUrl}/compliance/statement-of-applicability?standard=std-a";

        // User 1: compliance-reader on org-a -> sees org-a and its descendant org-eng (2 nodes).
        await using (var context = await NewContextAsync())
        {
            await SignInWithRecentSudoAsync(context, "reader-a");
            App.Authz.GrantComplianceReader("reader-a", "org-a");
            var page = await context.NewPageAsync();
            await page.GotoAsync(soaUrl);

            Assert.Equal(2, await page.Locator("table.soa-nodes tbody tr").CountAsync());
            Assert.Equal(1, await page.Locator("tr[data-node-id='org-a']").CountAsync());
            Assert.Equal(0, await page.Locator("tr[data-node-id='org-b']").CountAsync());
        }

        // User 2: compliance-reader on org-b -> sees only org-b (1 node), not org-a's subtree.
        await using (var context = await NewContextAsync())
        {
            await SignInWithRecentSudoAsync(context, "reader-b");
            App.Authz.GrantComplianceReader("reader-b", "org-b");
            var page = await context.NewPageAsync();
            await page.GotoAsync(soaUrl);

            Assert.Equal(1, await page.Locator("table.soa-nodes tbody tr").CountAsync());
            Assert.Equal(1, await page.Locator("tr[data-node-id='org-b']").CountAsync());
            Assert.Equal(0, await page.Locator("tr[data-node-id='org-a']").CountAsync());
        }
    }

    [RequiresEnvVarFact(EnvVar = E2EGate.EnvVar)]
    public async Task ZeroGrantCallerUnderEnforceSeesEmptyAccessibleSet()
    {
        Gate();

        // A dedicated app in Enforce mode, where a caller with NO grant sees the empty accessible set
        // (the Compat zero-grant read fallback is gone), alongside the Compat isolation above.
        await using var enforce = new E2EAppFixture { RegisterEmailSender = true, AuthzMode = "Enforce" };
        enforce.EnsureStarted();
        enforce.Compliance.Standards = [new StandardRow("std-a", "Standard A", "1.0", "Example Authority", null, null)];
        enforce.Compliance.Organisations = [new OrganisationRow("org-a", "Org A", "Company", null)];
        enforce.Compliance.Scopes = [new ScopeRow("scope-a", "Scope A", "org-a", "std-a", "In")];

        var user = E2EAppFixture.MakeUser("no-grant");
        var token = enforce.SeedSessionWithSudo(user); // no GrantComplianceReader: a zero-grant caller

        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.AddCookiesAsync([
            new Cookie
            {
                Name = "__Host-freeboard-session",
                Value = token,
                Url = enforce.BaseUrl,
                Secure = true,
                HttpOnly = true,
                SameSite = SameSiteAttribute.Strict,
            }
        ]);
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{enforce.BaseUrl}/compliance/statement-of-applicability?standard=std-a");

        // Enforce + zero grant -> nothing accessible: no org nodes are rendered.
        Assert.Equal(0, await page.Locator("table.soa-nodes tbody tr").CountAsync());
        Assert.Equal(0, await page.Locator("tr[data-node-id='org-a']").CountAsync());
    }
}
