using Freeboard.Compliance;
using Freeboard.Persistence;

namespace Freeboard.Web.Tests;

/// <summary>
/// The shared inclusive-ancestry walk. RBAC correctness depends on its cycle guard: a malformed
/// parent map (self-parent or a loop) must terminate rather than spin, and the returned chain must
/// run start -> ... -> root so a grant on any ancestor can be matched. The memoizing overload must
/// hold no state of its own, so a second pass over a changed tree cannot serve the first pass's chain.
/// </summary>
public sealed class AssetAncestryTests
{
    private static IReadOnlyDictionary<string, AssetNode> Map(params AssetNode[] rows)
        => rows.ToDictionary(r => r.Id, StringComparer.Ordinal);

    [Fact]
    public void ReturnsChainFromStartToRoot()
    {
        var byId = Map(TestAssets.Org("root"), TestAssets.Org("mid", "root"), TestAssets.Org("leaf", "mid"));

        var chain = AssetAncestry.InclusiveAncestors("leaf", byId);

        Assert.Equal(new[] { "leaf", "mid", "root" }, chain);
    }

    [Fact]
    public void UnknownStartIdYieldsJustThatId()
    {
        var byId = Map(TestAssets.Org("root"));

        var chain = AssetAncestry.InclusiveAncestors("ghost", byId);

        Assert.Equal(new[] { "ghost" }, chain);
    }

    [Fact]
    public void DanglingParentIdIsIncludedThenChainStops()
    {
        // leaf's parent id is not a real node: the walk appends the id (it cannot be resolved
        // further) and then terminates. Config validation reports an unknown parent separately.
        var byId = Map(TestAssets.Org("leaf", "absent"));

        var chain = AssetAncestry.InclusiveAncestors("leaf", byId);

        Assert.Equal(new[] { "leaf", "absent" }, chain);
    }

    [Fact]
    public void SelfParentTerminatesWithoutRepeating()
    {
        var byId = Map(TestAssets.Org("a", "a"));

        var chain = AssetAncestry.InclusiveAncestors("a", byId);

        Assert.Equal(new[] { "a" }, chain);
    }

    [Fact]
    public void TwoNodeCycleTerminatesWithEachNodeOnce()
    {
        var byId = Map(TestAssets.Org("a", "b"), TestAssets.Org("b", "a"));

        var chain = AssetAncestry.InclusiveAncestors("a", byId);

        Assert.Equal(new[] { "a", "b" }, chain);
    }

    [Fact]
    public void ChainCrossesANonOrganisationLink()
    {
        // The walk is edge-typed, not type-typed: a machine between two organisations is traversed.
        // Bounding the chain at that link is the write gate's job, not this helper's.
        var byId = Map(
            TestAssets.Org("company"), TestAssets.Org("dept", "company"),
            TestAssets.Machine("m1", "dept"), TestAssets.Org("far", "m1"));

        Assert.Equal(new[] { "far", "m1", "dept", "company" }, AssetAncestry.InclusiveAncestors("far", byId));
    }

    [Fact]
    public void MemoizedPassesOverDifferentTreesEachSeeTheirOwnInput()
    {
        // No static mutable state: the cache is a caller-supplied parameter, so a second pass over a
        // re-parented tree must not serve the first pass's chain for the same id.
        var first = Map(TestAssets.Org("root"), TestAssets.Org("leaf", "root"));
        var firstCache = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        Assert.Equal(new[] { "leaf", "root" }, AssetAncestry.InclusiveAncestors("leaf", first, firstCache));

        var second = Map(TestAssets.Org("other"), TestAssets.Org("leaf", "other"));
        var secondCache = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        Assert.Equal(new[] { "leaf", "other" }, AssetAncestry.InclusiveAncestors("leaf", second, secondCache));

        // The first pass's own cache is still consistent with the tree it was built from.
        Assert.Equal(new[] { "leaf", "root" }, AssetAncestry.InclusiveAncestors("leaf", first, firstCache));
    }

    [Fact]
    public void MemoizedChainMatchesThePureOne()
    {
        var byId = Map(TestAssets.Org("root"), TestAssets.Org("mid", "root"), TestAssets.Machine("m", "mid"));
        var cache = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        Assert.Equal(AssetAncestry.InclusiveAncestors("m", byId), AssetAncestry.InclusiveAncestors("m", byId, cache));
        Assert.Equal(AssetAncestry.InclusiveAncestors("m", byId), AssetAncestry.InclusiveAncestors("m", byId, cache));
    }

    [Fact]
    public void MemoizedReuseAcrossAJoinMatchesThePureChain()
    {
        // One walk stores a chain for every node it passes, so a sibling branch joining that path
        // appends the stored suffix instead of re-walking it. Both orders must match the pure walk.
        var byId = Map(
            TestAssets.Org("root"), TestAssets.Org("mid", "root"),
            TestAssets.Machine("a", "mid"), TestAssets.Machine("b", "mid"));
        var cache = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        Assert.Equal(new[] { "a", "mid", "root" }, AssetAncestry.InclusiveAncestors("a", byId, cache));
        Assert.Equal(new[] { "b", "mid", "root" }, AssetAncestry.InclusiveAncestors("b", byId, cache));
        Assert.Equal(new[] { "mid", "root" }, AssetAncestry.InclusiveAncestors("mid", byId, cache));

        // The other direction: the shared suffix is stored first, then the branches join it.
        var reverse = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        Assert.Equal(new[] { "root" }, AssetAncestry.InclusiveAncestors("root", byId, reverse));
        Assert.Equal(new[] { "a", "mid", "root" }, AssetAncestry.InclusiveAncestors("a", byId, reverse));
    }

    [Fact]
    public void MemoizedChainIntoACycleIsNotServedFromAShorterOne()
    {
        // A chain that ends by meeting itself is a suffix of nothing: the guard cut a node that a
        // longer chain entering the cycle further back still visits. Reusing it would re-add that node.
        var byId = Map(TestAssets.Org("a", "b"), TestAssets.Org("b", "c"), TestAssets.Org("c", "a"));
        var cache = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        Assert.Equal(new[] { "b", "c", "a" }, AssetAncestry.InclusiveAncestors("b", byId, cache));
        Assert.Equal(new[] { "a", "b", "c" }, AssetAncestry.InclusiveAncestors("a", byId, cache));

        // And a node feeding into a cycle it is not part of.
        var feeder = Map(TestAssets.Org("x", "b"), TestAssets.Org("b", "c"), TestAssets.Org("c", "b"));
        var feederCache = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        Assert.Equal(new[] { "b", "c" }, AssetAncestry.InclusiveAncestors("b", feeder, feederCache));
        Assert.Equal(new[] { "x", "b", "c" }, AssetAncestry.InclusiveAncestors("x", feeder, feederCache));
    }
}
