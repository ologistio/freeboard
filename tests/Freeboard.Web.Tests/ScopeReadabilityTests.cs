using System.Net.Http.Json;
using System.Text.Json;
using Freeboard.Persistence;

namespace Freeboard.Web.Tests;

/// <summary>
/// Subject-readability narrowing on the unified /scopes read. One membership test: a scope is
/// readable exactly when its subject asset is in the caller's accessible asset set, so every subject
/// kind narrows by the same rule and a subject that resolves to no live asset fails closed.
/// </summary>
public sealed class ScopeReadabilityTests
{
    private static async Task<HashSet<string>> VisibleScopeIdsAsync(HttpClient client)
    {
        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/scopes");
        return json.EnumerateArray().Select(s => s.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal);
    }

    private static ScopeRow ControlScope(string id, string subject) =>
        new(id, id, subject, null, null, "ctrl-a", "In", null);

    [Fact]
    public async Task MachineSubjectReadabilityNarrowsByParentAncestry()
    {
        var store = new FakeComplianceStore
        {
            Assets =
            [
                TestAssets.Org("org-a"),                                    // accessible
                TestAssets.Org("org-x"),                                    // not accessible
                TestAssets.Org("org-mid", "gone-org"),                      // accessible, dangling ancestor
                TestAssets.Machine("m-in", "org-a"),
                TestAssets.Machine("m-out", "org-x"),
                TestAssets.Machine("m-ghost", "ghost-org"),
                TestAssets.Machine("m-mid", "org-mid"),
                TestAssets.Machine("m-retired", "org-a", source: "discovered", state: "Retired"),
            ],
            Scopes =
            [
                ControlScope("s-in", "m-in"),           // (i) under accessible parent -> visible
                ControlScope("s-out", "m-out"),         // (ii) non-accessible ancestry -> omitted
                ControlScope("s-ghost", "m-ghost"),     // (iii) dangling parent -> omitted
                ControlScope("s-mid", "m-mid"),         // (iv) accessible parent, dangling ancestor -> visible
                ControlScope("s-retired", "m-retired"), // retired discovered -> no live anchor -> omitted
            ],
        };
        var authz = new FakeAuthzStore().GrantComplianceReader("u1", "org-a").GrantComplianceReader("u1", "org-mid");
        using var factory = new AuthWebFactory { Compliance = store, AuthzMode = "Enforce", Authz = authz };
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var visible = await VisibleScopeIdsAsync(client);

        Assert.Contains("s-in", visible);
        Assert.Contains("s-mid", visible);
        Assert.DoesNotContain("s-out", visible);
        Assert.DoesNotContain("s-ghost", visible);
        Assert.DoesNotContain("s-retired", visible);
    }

    [Fact]
    public async Task MachineSubjectWithCyclicParentNoAccessibleOrgIsOmitted()
    {
        // org-c1 <-> org-c2 form a parent cycle; the caller can access neither, so the machine hanging
        // under the cycle is omitted (the bounded walk finds no accessible org).
        var store = new FakeComplianceStore
        {
            Assets =
            [
                TestAssets.Org("org-a"),
                TestAssets.Org("org-c1", "org-c2"),
                TestAssets.Org("org-c2", "org-c1"),
                TestAssets.Machine("m-cyc", "org-c1"),
            ],
            Scopes = [ControlScope("s-cyc", "m-cyc")],
        };
        var authz = new FakeAuthzStore().GrantComplianceReader("u1", "org-a");
        using var factory = new AuthWebFactory { Compliance = store, AuthzMode = "Enforce", Authz = authz };
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var visible = await VisibleScopeIdsAsync(client);
        Assert.DoesNotContain("s-cyc", visible);
    }

    [Fact]
    public async Task OrgInaccessibleScopeHidesRowAndItsOutJustification()
    {
        var store = new FakeComplianceStore
        {
            Assets = [TestAssets.Org("org-a"), TestAssets.Org("org-b")],
            Scopes =
            [
                new ScopeRow("s-a", "Visible", "org-a", "std", null, null, "In", null),
                new ScopeRow("s-b", "Hidden out", "org-b", "std", null, null, "Out", "Secret reason."),
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

    [Fact]
    public async Task VendorSubjectNarrowsByOwnerAndAnOwnerlessVendorIsHidden()
    {
        var store = new FakeComplianceStore
        {
            Assets =
            [
                TestAssets.Org("org-a"),
                TestAssets.Org("org-x"),
                TestAssets.Vendor("v-mine", "org-a"),
                TestAssets.Vendor("v-theirs", "org-x"),
                TestAssets.Vendor("v-ownerless", null),
                TestAssets.Vendor("v-dangling", "gone-org"),
            ],
            Scopes =
            [
                ControlScope("s-mine", "v-mine"),
                ControlScope("s-theirs", "v-theirs"),
                ControlScope("s-ownerless", "v-ownerless"),
                ControlScope("s-dangling", "v-dangling"),
                ControlScope("s-absent", "v-not-an-asset"),
            ],
        };
        var authz = new FakeAuthzStore().GrantComplianceReader("u1", "org-a");
        using var factory = new AuthWebFactory { Compliance = store, AuthzMode = "Enforce", Authz = authz };
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var visible = await VisibleScopeIdsAsync(client);

        Assert.Equal(["s-mine"], visible.OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }
}
