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
}
