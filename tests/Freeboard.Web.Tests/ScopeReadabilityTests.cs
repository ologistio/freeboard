using System.Net.Http.Json;
using System.Text.Json;
using Freeboard.Compliance;
using Freeboard.Persistence;

namespace Freeboard.Web.Tests;

/// <summary>
/// Subject-readability narrowing on the unified /scopes read: every subject kind resolves to an
/// anchoring organisation, and a subject that resolves to none fails closed.
/// </summary>
public sealed class ScopeReadabilityTests
{
    private static async Task<HashSet<string>> VisibleScopeIdsAsync(HttpClient client)
    {
        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/scopes");
        return json.EnumerateArray().Select(s => s.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal);
    }

    // A machine-subject control scope with the enriched subject fields the readability branch needs.
    private static ScopeRow Machine(string id, string parent, string? source = null, string? state = null) =>
        new(id, id, "asset-" + id, null, null, "ctrl-a", "In", null,
            SubjectType: "Machine", SubjectSource: source, SubjectState: state, SubjectParent: parent);

    [Fact]
    public async Task MachineSubjectReadabilityNarrowsByParentAncestry()
    {
        var store = new FakeComplianceStore
        {
            Organisations =
            [
                new OrganisationRow("org-a", "Org A", "Company", null),          // accessible
                new OrganisationRow("org-x", "Org X", "Company", null),          // not accessible
                new OrganisationRow("org-mid", "Mid", "Company", "gone-org"),    // accessible, dangling ancestor
            ],
            Scopes =
            [
                Machine("m-in", "org-a"),                                        // (i) under accessible parent -> visible
                Machine("m-out", "org-x"),                                       // (ii) non-accessible ancestry -> omitted
                Machine("m-ghost", "ghost-org"),                                 // (iii) dangling parent -> omitted
                Machine("m-mid", "org-mid"),                                     // (iv) accessible parent, dangling ancestor -> visible
                Machine("m-retired", "org-a", source: "discovered", state: "Retired"), // retired discovered -> unresolved -> omitted
            ],
        };
        var authz = new FakeAuthzStore().GrantComplianceReader("u1", "org-a").GrantComplianceReader("u1", "org-mid");
        using var factory = new AuthWebFactory { Compliance = store, AuthzMode = "Enforce", Authz = authz };
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var visible = await VisibleScopeIdsAsync(client);

        Assert.Contains("m-in", visible);
        Assert.Contains("m-mid", visible);
        Assert.DoesNotContain("m-out", visible);
        Assert.DoesNotContain("m-ghost", visible);
        Assert.DoesNotContain("m-retired", visible);
    }

    [Fact]
    public async Task MachineSubjectWithCyclicParentNoAccessibleOrgIsOmitted()
    {
        // org-c1 <-> org-c2 form a parent cycle; the caller can access neither, so the machine hanging
        // under the cycle is omitted (the bounded walk finds no accessible org).
        var store = new FakeComplianceStore
        {
            Organisations =
            [
                new OrganisationRow("org-a", "Org A", "Company", null),
                new OrganisationRow("org-c1", "Cycle 1", "Company", "org-c2"),
                new OrganisationRow("org-c2", "Cycle 2", "Company", "org-c1"),
            ],
            Scopes = [Machine("m-cyc", "org-c1")],
        };
        var authz = new FakeAuthzStore().GrantComplianceReader("u1", "org-a");
        using var factory = new AuthWebFactory { Compliance = store, AuthzMode = "Enforce", Authz = authz };
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var visible = await VisibleScopeIdsAsync(client);
        Assert.DoesNotContain("m-cyc", visible);
    }

    [Fact]
    public async Task OrgInaccessibleScopeHidesRowAndItsOutJustification()
    {
        var store = new FakeComplianceStore
        {
            Organisations =
            [
                new OrganisationRow("org-a", "Org A", "Company", null),
                new OrganisationRow("org-b", "Org B", "Company", null),
            ],
            Scopes =
            [
                new ScopeRow("s-a", "Visible", "org-a", "std", null, null, "In", null, SubjectType: "Company"),
                new ScopeRow("s-b", "Hidden out", "org-b", "std", null, null, "Out", "Secret reason.", SubjectType: "Company"),
            ],
        };
        var authz = new FakeAuthzStore().GrantComplianceReader("u1", "org-a");
        using var factory = new AuthWebFactory { Compliance = store, AuthzMode = "Enforce", Authz = authz };
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var visible = await VisibleScopeIdsAsync(client);
        Assert.Equal(["s-a"], visible.OrderBy(x => x, StringComparer.Ordinal).ToArray());

        var raw = await client.GetStringAsync("/api/v1/freeboard/scopes");
        Assert.DoesNotContain("Secret reason.", raw, StringComparison.Ordinal);
    }

    private static readonly IReadOnlyDictionary<string, OrganisationRow> Orgs =
        new Dictionary<string, OrganisationRow>(StringComparer.Ordinal)
        {
            ["org-a"] = new("org-a", "A", "Company", null),
            ["org-x"] = new("org-x", "X", "Company", null),
            ["org-mid"] = new("org-mid", "Mid", "Company", "gone-org"),
            ["org-c1"] = new("org-c1", "C1", "Company", "org-c2"),
            ["org-c2"] = new("org-c2", "C2", "Company", "org-c1"),
        };

    private static IReadOnlySet<string> Accessible(params string[] ids) =>
        ids.ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void SubjectReadablePredicateCoversTheMachineBranches()
    {
        // Accessible parent -> admit.
        Assert.True(ComplianceEndpoints.SubjectReadable(Machine("m1", "org-a"), Accessible("org-a"), Orgs));

        // Parent outside the accessible set -> omit.
        Assert.False(ComplianceEndpoints.SubjectReadable(Machine("m2", "org-x"), Accessible("org-a"), Orgs));

        // Dangling parent (resolves to no org) -> omit.
        Assert.False(ComplianceEndpoints.SubjectReadable(Machine("m3", "ghost-org"), Accessible("org-a"), Orgs));

        // Accessible parent whose own ancestor is dangling -> still admit (the accessible parent grants it).
        Assert.True(ComplianceEndpoints.SubjectReadable(Machine("m4", "org-mid"), Accessible("org-mid"), Orgs));

        // Cyclic ancestry, no accessible org in the cycle -> omit (bounded walk finds no member).
        Assert.False(ComplianceEndpoints.SubjectReadable(Machine("m5", "org-c1"), Accessible("org-a"), Orgs));

        // Cyclic ancestry with a real accessible org in the cycle -> admit.
        Assert.True(ComplianceEndpoints.SubjectReadable(Machine("m6", "org-c1"), Accessible("org-c2"), Orgs));

        // A retired discovered machine is unresolved (not a live anchor) -> omit regardless of ancestry.
        Assert.False(ComplianceEndpoints.SubjectReadable(
            Machine("m7", "org-a", source: "discovered", state: "Retired"), Accessible("org-a"), Orgs));
    }
}
