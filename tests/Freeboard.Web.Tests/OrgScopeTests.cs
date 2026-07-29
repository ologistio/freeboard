using Freeboard.Compliance;
using Freeboard.Persistence;

namespace Freeboard.Web.Tests;

/// <summary>
/// Unit tests for the pure <see cref="OrgScope.InScopeIds"/> subtree helper: a root selects its whole
/// subtree, a leaf selects only itself, null is the accessible organisations, an unknown id is empty, a
/// cyclic parent link terminates, a restricted accessible set bounds both "All" and a selected subtree,
/// and neither branch admits a non-organisation asset.
/// </summary>
public sealed class OrgScopeTests
{
    // company -> dept -> team, plus a sibling company. Ordered arbitrarily to exercise the walk.
    private static IReadOnlyList<AssetNode> Tree() =>
    [
        TestAssets.Org("company", title: "Company"),
        TestAssets.Org("dept", "company", "Department", "Department"),
        TestAssets.Org("team", "dept", "Department", "Team"),
        TestAssets.Org("other", title: "Other Company"),
    ];

    private static IReadOnlySet<string> All(IReadOnlyList<AssetNode> assets) =>
        assets.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void RootSelectsWholeSubtree()
    {
        var orgs = Tree();
        var scope = OrgScope.InScopeIds(orgs, All(orgs), "company");
        Assert.Equal(new HashSet<string> { "company", "dept", "team" }, scope);
    }

    [Fact]
    public void LeafSelectsOnlyItself()
    {
        var orgs = Tree();
        var scope = OrgScope.InScopeIds(orgs, All(orgs), "team");
        Assert.Equal(new HashSet<string> { "team" }, scope);
    }

    [Fact]
    public void NullSelectsAccessibleSet()
    {
        var orgs = Tree();
        var scope = OrgScope.InScopeIds(orgs, All(orgs), null);
        Assert.Equal(All(orgs), scope);
    }

    [Fact]
    public void UnknownIdYieldsEmpty()
    {
        var orgs = Tree();
        var scope = OrgScope.InScopeIds(orgs, All(orgs), "missing");
        Assert.Empty(scope);
    }

    [Fact]
    public void CyclicParentLinksTerminate()
    {
        // a <-> b cycle: each is the other's parent. Both are reachable only from within the cycle.
        IReadOnlyList<AssetNode> orgs = [TestAssets.Org("a", "b"), TestAssets.Org("b", "a")];
        var scope = OrgScope.InScopeIds(orgs, All(orgs), "a");
        Assert.Equal(new HashSet<string> { "a", "b" }, scope);
    }

    [Fact]
    public void RestrictedAccessibleSetBoundsNullSelection()
    {
        var orgs = Tree();
        IReadOnlySet<string> accessible = new HashSet<string>(StringComparer.Ordinal) { "company", "dept" };
        var scope = OrgScope.InScopeIds(orgs, accessible, null);
        Assert.Equal(accessible, scope);
        Assert.DoesNotContain("team", scope);
        Assert.DoesNotContain("other", scope);
    }

    [Fact]
    public void RestrictedAccessibleSetBoundsSelectedSubtree()
    {
        var orgs = Tree();
        // team is accessible-excluded, so the company subtree drops it.
        IReadOnlySet<string> accessible = new HashSet<string>(StringComparer.Ordinal) { "company", "dept" };
        var scope = OrgScope.InScopeIds(orgs, accessible, "company");
        Assert.Equal(new HashSet<string> { "company", "dept" }, scope);
    }

    [Fact]
    public void NullSelectionExcludesAccessibleMachinesAndVendors()
    {
        // The null branch returns the accessible set, which is an ASSET set: a readable machine and a
        // readable vendor are in it and must not become in-scope organisations.
        IReadOnlyList<AssetNode> assets =
        [
            .. Tree(), TestAssets.Machine("m-1", "dept"), TestAssets.Vendor("v-1", "company"),
        ];

        var scope = OrgScope.InScopeIds(assets, All(assets), null);

        Assert.Equal(new HashSet<string> { "company", "dept", "team", "other" }, scope);
    }

    [Fact]
    public void SelectedSubtreeWalkDoesNotDescendThroughAMachine()
    {
        // far hangs off a machine that hangs off dept. The descend walk is organisation-only, so
        // selecting company must not reach the machine or anything parented onto it.
        IReadOnlyList<AssetNode> assets =
        [
            .. Tree(), TestAssets.Machine("m-1", "dept"), TestAssets.Org("far", "m-1"),
            TestAssets.Vendor("v-1", "company"),
        ];

        var scope = OrgScope.InScopeIds(assets, All(assets), "company");

        Assert.Equal(new HashSet<string> { "company", "dept", "team" }, scope);
    }

    [Fact]
    public void SelectingAReadableMachineYieldsNothing()
    {
        IReadOnlyList<AssetNode> assets = [.. Tree(), TestAssets.Machine("m-1", "dept")];

        Assert.Empty(OrgScope.InScopeIds(assets, All(assets), "m-1"));
    }
}
