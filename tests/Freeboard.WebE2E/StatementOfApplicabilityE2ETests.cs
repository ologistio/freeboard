using Freeboard.Persistence;
using Freeboard.TestInfrastructure;
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

        App.Compliance.Standards = [new StandardRow("std-a", "Standard A")];
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
}
