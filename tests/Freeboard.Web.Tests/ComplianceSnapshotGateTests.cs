using System.Net;
using System.Net.Http.Json;
using Freeboard.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Freeboard.Web.Tests;

/// <summary>
/// What an authorization gate reads, pinned as an exact set per gate path.
///
/// Every route- or body-anchored organisation gate and both role-assignment guards name
/// <c>Assets</c> and no payload set, so a schema missing a payload table degrades the surface that
/// reads it rather than closing every gated write. The one gate that cannot hold that rule is the scope
/// write, whose owning organisation is knowable only from the stored row: it names exactly
/// <c>Assets | Scopes</c>, and the exactness is the point - the exception must not be able to grow.
///
/// The same cases cover the stored-owner lookup on all FOUR of its call sites: the two DELETE
/// selectors and the two PUT handlers. A PUT authorizes the stored owner in-handler when the body moves
/// a row between organisations, and it does that on the snapshot the row came from, so the cross-org
/// path costs no extra read.
///
/// Naming the sets is not enough on its own, so the last region asserts the OUTCOME: the store serves a
/// different asset tree on a second read, and the gate's answer says which tree it walked. A gate
/// anchored on the request's earlier assets-only read would permit a cross-org move the stored row's own
/// snapshot refuses.
/// </summary>
public sealed class ComplianceSnapshotGateTests
{
    private const string Api = "/api/v1/freeboard";

    private const ComplianceReadSet StoredScopeGate = ComplianceReadSet.Assets | ComplianceReadSet.Scopes;

    private static FakeComplianceStore Store() => new()
    {
        Assets = [TestAssets.Org("org-a", title: "Org A"), TestAssets.Org("org-b", title: "Org B")],
        Scopes = StoredScopes(),
    };

    /// <summary>One stored row per route, both owned by org-a, so a body naming org-b is a cross-org move.</summary>
    private static IReadOnlyList<ScopeRow> StoredScopes() =>
    [
        new ScopeRow("scope-a", "Scope A", "org-a", "std-a", null, null, "In", null),
        new ScopeRow("rs-a", "RS A", "org-a", null, "req-a", null, "In", null),
    ];

    private static HttpClient AdminClient(AuthWebFactory factory)
        => factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("admin1", role: "admin"));

    #region gates that read the assets alone

    [Fact]
    public async Task ARouteAnchoredOrganisationGateNamesTheAssetsAndNoPayloadSet()
    {
        var store = Store();
        using var factory = new WriteFactory(new FakeComplianceWriteStore()) { Compliance = store };
        await SnapshotReadProbe.BootAsync(factory, store);
        using var client = AdminClient(factory);

        var response = await client.DeleteAsync($"{Api}/organisations/org-a");

        response.EnsureSuccessStatusCode();
        Assert.Equal([ComplianceReadSet.Assets], SnapshotReadProbe.RequestReads(store).Sets);
    }

    [Fact]
    public async Task ABodyAnchoredOrganisationGateNamesTheAssetsAndNoPayloadSet()
    {
        var store = Store();
        using var factory = new WriteFactory(new FakeComplianceWriteStore()) { Compliance = store };
        await SnapshotReadProbe.BootAsync(factory, store);
        using var client = AdminClient(factory);

        var response = await client.PutAsJsonAsync(
            $"{Api}/organisations/org-a", new { title = "Org A", kind = "Company", parent = (string?)null });

        response.EnsureSuccessStatusCode();
        Assert.Equal([ComplianceReadSet.Assets], SnapshotReadProbe.RequestReads(store).Sets);
    }

    [Fact]
    public async Task ARoleAssignmentGuardNamesTheAssetsAndNoPayloadSet()
    {
        var store = Store();
        using var factory = new AuthWebFactory { Compliance = store };
        await SnapshotReadProbe.BootAsync(factory, store);
        using var client = AdminClient(factory);

        var response = await client.PutAsJsonAsync(
            $"{Api}/organisations/org-a/role-assignments", new { user_id = "u1", role_key = "compliance-reader" });

        response.EnsureSuccessStatusCode();
        Assert.Equal([ComplianceReadSet.Assets], SnapshotReadProbe.RequestReads(store).Sets);
    }

    [Fact]
    public async Task TheRoleAssignmentPageGuardNamesTheAssetsAndNoPayloadSet()
    {
        // The OTHER role-assignment guard: the page handler gates in-process, because pipeline policies
        // do not run for page handlers. A caller holding nothing is denied, and the bare 403 renders no
        // view, so the guard's own read is the whole of the request's read sequence.
        var store = Store();
        using var factory = new AuthWebFactory { Compliance = store };
        await SnapshotReadProbe.BootAsync(factory, store);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var token = factory.SeedSession(AuthWebFactory.MakeUser("u1"));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/settings/role-assignments?orgId=org-a");
        request.Headers.Add("Cookie", $"{SessionCookie.Name}={token}");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal([ComplianceReadSet.Assets], SnapshotReadProbe.RequestReads(store).Sets);
    }

    #endregion

    #region the stored-row gate

    [Theory]
    [InlineData("scopes/scope-a")]
    [InlineData("requirement-scopes/rs-a")]
    public async Task AScopeDeleteSelectorNamesExactlyTheAssetsAndTheScopes(string route)
    {
        // A request that reads nothing else: the DELETE selector runs before anything in the request, so
        // this sequence is the whole of the gate's read. Exactly two sets, so the narrowing that lets one
        // gate reach a payload table cannot widen to a third.
        var store = Store();
        using var factory = new WriteFactory(new FakeComplianceWriteStore()) { Compliance = store };
        await SnapshotReadProbe.BootAsync(factory, store);
        using var client = AdminClient(factory);

        var response = await client.DeleteAsync($"{Api}/{route}");

        response.EnsureSuccessStatusCode();
        Assert.Equal([StoredScopeGate], SnapshotReadProbe.RequestReads(store).Sets);
    }

    [Theory]
    [InlineData("scopes/scope-a", "standard", "std-a")]
    [InlineData("requirement-scopes/rs-a", "requirement", "req-a")]
    public async Task AScopePutAuthorizesTheStoredOwnerOnOneAssetsAndScopesSnapshot(
        string route, string targetField, string targetId)
    {
        // The body moves the row from org-a to org-b, which is the path that authorizes the STORED owner
        // in-handler. That gate is anchored on the snapshot the stored row came from, so the cross-org
        // path takes no third read: the body-anchored selector's assets, then one Assets | Scopes read.
        var store = Store();
        using var factory = new WriteFactory(new FakeComplianceWriteStore()) { Compliance = store };
        await SnapshotReadProbe.BootAsync(factory, store);
        using var client = AdminClient(factory);

        var response = await client.PutAsJsonAsync($"{Api}/{route}", new Dictionary<string, string>
        {
            ["title"] = "Moved",
            ["subject"] = "org-b",
            [targetField] = targetId,
            ["disposition"] = "In",
        });

        response.EnsureSuccessStatusCode();
        Assert.Equal(
            [ComplianceReadSet.Assets, StoredScopeGate], SnapshotReadProbe.RequestReads(store).Sets);
    }

    #endregion

    #region the ancestry the stored-row gate walks

    // The organisation the caller may write. Every tree below is rooted here or at a root it holds
    // nothing on, so a subject moving between the two flips the authorization outcome.
    private const string Granted = "grant-root";

    private const string Ungranted = "other-root";

    /// <summary>
    /// The tree the store serves, with the stored row's subject hung off <paramref name="ownerParent"/>.
    /// The body's new subject stays under the granted root throughout, so the body-anchored gate that
    /// runs first always permits and only the stored-owner gate can refuse.
    /// </summary>
    private static IReadOnlyList<AssetNode> Tree(string ownerParent) =>
    [
        TestAssets.Org(Granted),
        TestAssets.Org(Ungranted),
        TestAssets.Org("org-a", ownerParent, "Department"),
        TestAssets.Org("org-b", Granted, "Department"),
    ];

    [Theory]
    [InlineData("scopes/scope-a", "standard", "std-a", true, HttpStatusCode.Forbidden)]
    [InlineData("scopes/scope-a", "standard", "std-a", false, HttpStatusCode.NoContent)]
    [InlineData("requirement-scopes/rs-a", "requirement", "req-a", true, HttpStatusCode.Forbidden)]
    [InlineData("requirement-scopes/rs-a", "requirement", "req-a", false, HttpStatusCode.NoContent)]
    public async Task AScopePutGatesTheStoredOwnerOnTheAncestryOfItsOwnSnapshot(
        string route, string targetField, string targetId, bool storedSnapshotMovesTheOwnerOut,
        HttpStatusCode expected)
    {
        // A PUT reads twice: the body-anchored selector takes the assets, then the stored-owner lookup
        // takes the assets AND the scopes. The store serves a different tree the second time, so the two
        // reads disagree about where the stored owner hangs - and the outcome names which one the gate
        // walked. Anchored on the stored row's snapshot, the moved-out case refuses. Anchored on the
        // request's earlier assets-only read it would permit, and the write would land.
        var writes = new FakeComplianceWriteStore();
        using var factory = new WriteFactory(writes)
        {
            Authz = new FakeAuthzStore().GrantOrgOwner("u1", Granted),
            Compliance = new FakeComplianceStore
            {
                Assets = Tree(Granted),
                AssetsAfterFirstRead = Tree(storedSnapshotMovesTheOwnerOut ? Ungranted : Granted),
                Scopes = StoredScopes(),
            },
        };
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var response = await client.PutAsJsonAsync($"{Api}/{route}", new Dictionary<string, string>
        {
            ["title"] = "Moved",
            ["subject"] = "org-b",
            [targetField] = targetId,
            ["disposition"] = "In",
        });

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(
            expected == HttpStatusCode.NoContent,
            (writes.LastScopeId ?? writes.LastRequirementScopeId) is not null);
    }

    [Theory]
    [InlineData("scopes/scope-a")]
    [InlineData("requirement-scopes/rs-a")]
    public async Task AScopeDeleteGatesTheStoredOwnerOnTheTreeTheRequestRead(string route)
    {
        // The gate walks a tree at all, and walks the one the request read: the DELETE selector runs
        // before anything else, so its Assets | Scopes snapshot is the request's first asset read, and the
        // tree the store would serve on a SECOND asset read never reaches the gate. A selector that took
        // its own second read would see the granted tree and permit.
        //
        // This does not separate the two anchoring forms. On a DELETE the stored-owner lookup IS the first
        // asset read, so a snapshot-anchored walk and a shared-read-anchored walk get the same list. That
        // the gate anchors on the row's own snapshot is pinned by
        // AuthzRequestCacheTests.AnOrganisationResourceBuiltFromASnapshotWalksThatSnapshotsAssets.
        using var factory = new WriteFactory(new FakeComplianceWriteStore())
        {
            Authz = new FakeAuthzStore().GrantOrgOwner("u1", Granted),
            Compliance = new FakeComplianceStore
            {
                Assets = Tree(Ungranted),
                AssetsAfterFirstRead = Tree(Granted),
                Scopes = StoredScopes(),
            },
        };
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var response = await client.DeleteAsync($"{Api}/{route}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    #endregion
}
