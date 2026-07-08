using Deque.AxeCore.Playwright;
using Freeboard.Persistence;
using Freeboard.TestInfrastructure;
using Microsoft.Playwright;
using Xunit;

namespace Freeboard.WebE2E;

/// <summary>
/// Browser E2E for the read-only Statement of Applicability page. Gated: a plain <c>dotnet test</c>
/// with no browser skips. The page requires an authenticated user and is GET-only; it reads through
/// the injected compliance store (no MySQL), so seeding a signed-in session plus the fake store is
/// enough to drive it.
/// </summary>
[Trait("Category", TestCategories.E2E)]
public sealed class StatementOfApplicabilityE2ETests : E2ETestBase
{
    [RequiresEnvVarFact(EnvVar = E2EGate.EnvVar)]
    public async Task ChoosingAStandard_RendersResolvedNodesOrderedById()
    {
        Gate();

        App.Compliance.Standards = [new StandardRow("std-a", "Standard A", "1.0", "Example Authority", null, null)];
        App.Compliance.Organisations =
        [
            new OrganisationRow("org-a", "Org A", "Company", null),
            new OrganisationRow("org-eng", "Engineering", "Department", "org-a"),
        ];
        App.Compliance.Scopes = [new ScopeRow("scope-a", "Scope A", "org-a", "std-a", "In")];

        await using var context = await NewContextAsync();
        // The page requires an authenticated user; seed a session and set its cookie before navigating.
        await SignInWithRecentSudoAsync(context, "soa-e2e");
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{App.BaseUrl}/compliance/statement-of-applicability?standard=std-a");

        var rows = page.Locator("table.soa-nodes tbody tr");
        Assert.Equal(2, await rows.CountAsync());

        // Ordered by id: org-a (explicit In) then org-eng (inherited In).
        Assert.Equal("org-a", await rows.Nth(0).GetAttributeAsync("data-node-id"));
        Assert.Contains("explicit", await rows.Nth(0).InnerTextAsync(), StringComparison.Ordinal);
        Assert.Contains("In", await rows.Nth(0).InnerTextAsync(), StringComparison.Ordinal);

        Assert.Equal("org-eng", await rows.Nth(1).GetAttributeAsync("data-node-id"));
        Assert.Contains("inherited", await rows.Nth(1).InnerTextAsync(), StringComparison.Ordinal);
    }

    [RequiresEnvVarFact(EnvVar = E2EGate.EnvVar)]
    public async Task SelectingAnOrganisation_FiltersToItsSubtree_AndPersistsAcrossNavigation()
    {
        Gate();

        App.Compliance.Standards = [new StandardRow("std-a", "Standard A", "1.0", "Example Authority", null, null)];
        App.Compliance.Organisations =
        [
            new OrganisationRow("org-a", "Org A", "Company", null),
            new OrganisationRow("org-eng", "Engineering", "Department", "org-a"),
            new OrganisationRow("org-b", "Org B", "Company", null),
        ];
        App.Compliance.Scopes = [new ScopeRow("scope-a", "Scope A", "org-a", "std-a", "In")];

        await using var context = await NewContextAsync();
        await SignInWithRecentSudoAsync(context, "soa-scope-e2e");
        var page = await context.NewPageAsync();

        var soaUrl = $"{App.BaseUrl}/compliance/statement-of-applicability?standard=std-a";
        await page.GotoAsync(soaUrl);

        // All Organisations: the whole tree renders (3 nodes).
        Assert.Equal(3, await page.Locator("table.soa-nodes tbody tr").CountAsync());

        // Select the top-level Org A: filters the table to org-a plus its descendant org-eng (2 nodes),
        // dropping the sibling org-b. The selector link redirects back to the SoA page.
        await page.GetByRole(AriaRole.Link, new() { Name = "Org A" }).ClickAsync();
        await page.WaitForFunctionAsync(
            "() => document.querySelectorAll('table.soa-nodes tbody tr').length === 2");
        Assert.Equal("Org A", await page.Locator("[data-active-scope]").InnerTextAsync());
        Assert.Equal(0, await page.Locator("tr[data-node-id='org-b']").CountAsync());

        // Selection persists across a navigation away and back (carried by the cookie, not the URL).
        await page.GotoAsync($"{App.BaseUrl}/home");
        await page.GotoAsync(soaUrl);
        Assert.Equal(2, await page.Locator("table.soa-nodes tbody tr").CountAsync());
        Assert.Equal("Org A", await page.Locator("[data-active-scope]").InnerTextAsync());

        // All Organisations restores the full tree.
        await page.GetByRole(AriaRole.Link, new() { Name = "All Organisations" }).ClickAsync();
        await page.WaitForFunctionAsync(
            "() => document.querySelectorAll('table.soa-nodes tbody tr').length === 3");
        Assert.Equal("All Organisations", await page.Locator("[data-active-scope]").InnerTextAsync());
    }

    [RequiresEnvVarFact(EnvVar = E2EGate.EnvVar)]
    public async Task DrillingDown_RevealsRequirementsControlsChecks_AndCollapsesAgain()
    {
        Gate();

        App.Compliance.Standards = [new StandardRow("std-a", "Standard A", "1.0", "Example Authority", null, null)];
        App.Compliance.Organisations = [new OrganisationRow("org-a", "Org A", "Company", null)];
        App.Compliance.Scopes = [new ScopeRow("scope-a", "Scope A", "org-a", "std-a", "In")];
        App.Compliance.Requirements =
        [
            new RequirementRow("req-a", "Requirement A", "std-a", "Theme", "Do the thing.", null, "L", "https://example.com/a"),
        ];
        App.Compliance.Controls = [new ControlRow("ctrl-a", "Control A", ["req-a"], "all")];
        App.Compliance.Collectors =
        [
            new EvidenceCollectorRow("coll-a", "Collector A", "ctrl-a", null, "integration", "daily", null, new Dictionary<string, string>()),
        ];
        App.Compliance.Templates =
        [
            new AttestationTemplateRow("tmpl-a", "Template A", "ctrl-a", "manual", null, [], null, []),
        ];

        await using var context = await NewContextAsync();
        await SignInWithRecentSudoAsync(context, "soa-drill-e2e");
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{App.BaseUrl}/compliance/statement-of-applicability?standard=std-a");

        var reqRow = page.Locator("li[data-requirement-id='req-a']");
        var ctrlRow = page.Locator("li[data-control-id='ctrl-a']");
        var collectorCheck = page.Locator("li[data-check-kind='collector']");
        var attestationCheck = page.Locator("li[data-check-kind='attestation']");

        // All collapsed on load: the nested rows are server-rendered into the DOM but hidden.
        Assert.False(await reqRow.IsVisibleAsync());

        var orgToggle = page.GetByRole(AriaRole.Button, new() { Name = "Toggle organisation org-a" });
        Assert.Equal("false", await orgToggle.GetAttributeAsync("aria-expanded"));
        await orgToggle.ClickAsync();
        Assert.Equal("true", await orgToggle.GetAttributeAsync("aria-expanded"));
        await reqRow.WaitForAsync(new() { State = WaitForSelectorState.Visible });

        await page.GetByRole(AriaRole.Button, new() { Name = "Toggle requirement req-a" }).ClickAsync();
        await ctrlRow.WaitForAsync(new() { State = WaitForSelectorState.Visible });

        await page.GetByRole(AriaRole.Button, new() { Name = "Toggle control ctrl-a" }).ClickAsync();
        await collectorCheck.WaitForAsync(new() { State = WaitForSelectorState.Visible });

        // Both check kinds are visible once fully expanded.
        Assert.True(await collectorCheck.IsVisibleAsync());
        Assert.True(await attestationCheck.IsVisibleAsync());

        // Axe audit on the fully expanded tree.
        var result = await page.RunAxe();
        Assert.True(
            result.Violations.Length == 0,
            $"{result.Violations.Length} accessibility violation(s) on the expanded SoA drill-down");

        // Collapsing the organisation hides the whole subtree again.
        await orgToggle.ClickAsync();
        Assert.Equal("false", await orgToggle.GetAttributeAsync("aria-expanded"));
        await reqRow.WaitForAsync(new() { State = WaitForSelectorState.Hidden });
    }
}
